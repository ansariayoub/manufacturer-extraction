using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Models;
using ManufacturerExtraction.Api.Services;
using ManufacturerExtraction.Api.Services.Auth;
using ManufacturerExtraction.Api.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// --- Sign-in (Microsoft Entra) -------------------------------------------------------------
// Only enforced once AzureAd:TenantId/ClientId are configured; until then the API stays open and
// every caller is an admin of the default client (see AuthSettings).
var authSettings = new AuthSettings(builder.Configuration);
builder.Services.AddSingleton(authSettings);

if (authSettings.Enabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{authSettings.TenantId}/v2.0";
            options.MapInboundClaims = false; // keep Entra's own claim names (oid, preferred_username, ...)
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidAudiences = new[] { authSettings.ClientId, $"api://{authSettings.ClientId}" },
                // Entra issues v1 (sts.windows.net) or v2 (login.microsoftonline.com) access tokens
                // depending on the app registration's manifest; accept either so the API does not
                // depend on that setting.
                ValidIssuers = new[]
                {
                    $"https://login.microsoftonline.com/{authSettings.TenantId}/v2.0",
                    $"https://sts.windows.net/{authSettings.TenantId}/",
                },
            };
        });
}

builder.Services.AddAuthorization(options =>
{
    if (authSettings.Enabled)
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddScoped<CurrentUserAccessor>();
builder.Services.AddScoped<AppUserResolver>();

builder.Services.AddControllers(options => options.Filters.Add<AppUserFilter>());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// The viewer's tabs return large JSON payloads (canonical lines, the raw extraction); compressing
// them cuts their size roughly tenfold, which is most of the wait on a big file.
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);

// Holds the parsed/pretty-printed view of a large document for a few minutes so paging through it
// does not re-read and re-parse the database blob on every "Next" click. Size-limited (bytes,
// estimated per entry) so a handful of huge files cannot exhaust the App Service's memory.
builder.Services.AddMemoryCache(options => options.SizeLimit = 150_000_000);

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
              .AllowAnyMethod()
              // Lets the browser read the file name the export endpoints suggest.
              .WithExposedHeaders("Content-Disposition"));
});

builder.Services.AddSingleton<IProcessingCancellationRegistry, ProcessingCancellationRegistry>();
builder.Services.AddSingleton<IDocumentProcessingQueue, DocumentProcessingQueue>();
// Shared across every document being processed, not per document — see OpenAiConcurrencyLimiter
// for why AnalyticsTransformationService's own local semaphore (it is AddScoped) wasn't enough.
builder.Services.AddSingleton<OpenAiConcurrencyLimiter>();
// Settings-page-controlled choice of Azure OpenAI deployment — see AiModelSettingsService.
builder.Services.AddSingleton<AiModelSettingsService>();
builder.Services.AddSingleton<ExtractionPromptSettingsService>();
builder.Services.AddSingleton<EmbeddingModelSettingsService>();
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

app.UseResponseCompression();
app.UseCors();
if (authSettings.Enabled) app.UseAuthentication();
app.UseAuthorization();
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
