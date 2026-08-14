using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Models;
using ManufacturerExtraction.Api.Services;
using ManufacturerExtraction.Api.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("AzureSql"),
        sqlOptions => sqlOptions
            .EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(15),
                errorNumbersToAdd: null)
            // The default 30s command timeout is too short for the largest source files: a single
            // RawExtraction write for a ~75k-row workbook has been observed taking ~48s, which
            // always failed with "Timeout expired" right as it was about to succeed. 180s gives
            // large writes (and the occasional Azure SQL serverless cold-start) enough room without
            // masking a genuinely hung query forever.
            .CommandTimeout(180)));

builder.Services.AddHttpClient<IContentUnderstandingService, ContentUnderstandingService>();

builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();
builder.Services.AddScoped<ISpreadsheetExtractionService, SpreadsheetExtractionService>();
builder.Services.AddScoped<ICumulativePeriodService, CumulativePeriodService>();
builder.Services.AddScoped<IAnalyticsTransformationService, AnalyticsTransformationService>();
builder.Services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(
                  "http://localhost:5173",
                  "https://agreeable-moss-0065fd60f.7.azurestaticapps.net")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

builder.Services.AddSingleton<IProcessingCancellationRegistry, ProcessingCancellationRegistry>();
builder.Services.AddSingleton<IDocumentProcessingQueue, DocumentProcessingQueue>();
builder.Services.AddHostedService<DocumentProcessingWorker>();

var app = builder.Build();

// Fail fast on bad config instead of starting successfully and failing per-request later.
// See StartupConfigValidator for why this exists.
StartupConfigValidator.ValidateOrExit(
    app.Configuration,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StartupConfigValidator"));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// One-time backfill: documents finished before the aggregate columns existed have them null,
// and the queue listing now reads those columns instead of re-parsing the canonical JSON — so
// without this they would show blank totals. Runs once (the WHERE clause stops matching after).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger("AggregateBackfill");

    var idsToBackfill = await db.Documents
        .Where(d => d.ProcessingStatus == ProcessingStatus.Done
                 && d.LineCount == null
                 && d.AnalyticsExtraction != null)
        .Select(d => d.Id)
        .ToListAsync();

    if (idsToBackfill.Count > 0)
        logger.LogInformation("Backfilling aggregate totals for {Count} document(s)", idsToBackfill.Count);

    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    // One document at a time — each AnalyticsJson can be several megabytes, so loading them all
    // at once would be the very problem this change exists to remove.
    foreach (var id in idsToBackfill)
    {
        try
        {
            var document = await db.Documents
                .Include(d => d.AnalyticsExtraction)
                .FirstAsync(d => d.Id == id);

            var report = JsonSerializer.Deserialize<AnalyticsReport>(
                document.AnalyticsExtraction!.AnalyticsJson, jsonOptions);

            document.ApplyAggregates(report?.Sales ?? new List<AnalyticsTransaction>());
            await db.SaveChangesAsync();

            db.Entry(document.AnalyticsExtraction).State = EntityState.Detached;
            db.Entry(document).State = EntityState.Detached;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not backfill aggregates for document {DocumentId}", id);
        }
    }
}

// Re-queue any document left mid-flight by a previous run (e.g. the API was restarted while a
// batch was processing). Without this they stay on Queued/Extracting/Mapping forever.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var queue = app.Services.GetRequiredService<IDocumentProcessingQueue>();

    var stuckIds = await db.Documents
        .Where(d => d.ProcessingStatus == ProcessingStatus.Queued
                 || d.ProcessingStatus == ProcessingStatus.Extracting
                 || d.ProcessingStatus == ProcessingStatus.Mapping)
        .OrderBy(d => d.UploadDate)
        .Select(d => d.Id)
        .ToListAsync();

    foreach (var id in stuckIds)
        queue.Enqueue(id);
}

app.Run();
