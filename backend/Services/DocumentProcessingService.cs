using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Models;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Services;

public class DocumentProcessingService : IDocumentProcessingService
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService _blobStorage;
    private readonly IContentUnderstandingService _contentUnderstanding;
    private readonly ISpreadsheetExtractionService _spreadsheetExtraction;
    private readonly IAnalyticsTransformationService _analyticsTransformation;
    private readonly ILogger<DocumentProcessingService> _logger;

    private static readonly string[] SpreadsheetTypes = { ".xlsx", ".xls" };

    public DocumentProcessingService(
        AppDbContext db,
        IBlobStorageService blobStorage,
        IContentUnderstandingService contentUnderstanding,
        ISpreadsheetExtractionService spreadsheetExtraction,
        IAnalyticsTransformationService analyticsTransformation,
        ILogger<DocumentProcessingService> logger)
    {
        _db = db;
        _blobStorage = blobStorage;
        _contentUnderstanding = contentUnderstanding;
        _spreadsheetExtraction = spreadsheetExtraction;
        _analyticsTransformation = analyticsTransformation;
        _logger = logger;
    }

    public async Task ProcessAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await _db.Documents
            .Include(d => d.RawExtraction)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new InvalidOperationException($"Document {documentId} not found");

        try
        {
            string rawJson;

            // Reuse the stored raw extraction when we already have one. This is what makes
            // "re-analyze with new instructions" fast and free: Content Understanding is the
            // permanent source of truth and does not need to run twice on the same file.
            if (document.RawExtraction is not null)
            {
                _logger.LogInformation("Reusing stored raw extraction for document {DocumentId}", documentId);
                rawJson = document.RawExtraction.RawJson;

                document.ProcessingStatus = ProcessingStatus.Mapping;
                document.ProgressPct = 55;
                await _db.SaveChangesAsync(ct);
            }
            else
            {
                document.ProcessingStatus = ProcessingStatus.Extracting;
                document.ProgressPct = 10;
                await _db.SaveChangesAsync(ct);

                if (SpreadsheetTypes.Contains(document.FileType.ToLowerInvariant()))
                {
                    // Spreadsheets are read cell by cell here rather than sent to Content
                    // Understanding — see SpreadsheetExtractionService for why.
                    var sheetFilter = CustomInstructionsParser.TryExtractSheetFilter(document.CustomInstructions);
                    if (sheetFilter is not null)
                        _logger.LogInformation(
                            "Restricting document {DocumentId} extraction to sheet '{Sheet}' per custom instructions",
                            documentId, sheetFilter);

                    await using var stream = await _blobStorage.DownloadAsync(document.BlobUrl);
                    rawJson = await _spreadsheetExtraction.ExtractAsync(stream, document.OriginalFileName, ct, sheetFilter);
                }
                else
                {
                    var sasUrl = await _blobStorage.GenerateSasUrlAsync(document.BlobUrl, TimeSpan.FromHours(2));
                    rawJson = await _contentUnderstanding.SubmitAndPollAsync(sasUrl, ct);
                }

                _db.RawExtractions.Add(new RawExtraction
                {
                    Id = Guid.NewGuid(),
                    DocumentId = document.Id,
                    RawJson = rawJson,
                    CreatedDate = DateTime.UtcNow
                });

                document.ProgressPct = 50;
                await _db.SaveChangesAsync(ct);

                document.ProcessingStatus = ProcessingStatus.Mapping;
                document.ProgressPct = 60;
                await _db.SaveChangesAsync(ct);
            }

            // The document is processed in chunks internally (see AnalyticsTransformationService),
            // so this callback fires once per chunk instead of jumping straight from 60% to 100%.
            async Task OnChunkProgress(double fraction)
            {
                document.ProgressPct = 60 + (fraction * 35);
                await _db.SaveChangesAsync(ct);
            }

            var result = await _analyticsTransformation.TransformAsync(
                rawJson,
                document.Manufacturer,
                document.PeriodMonth,
                document.PeriodYear,
                document.CustomInstructions,
                OnChunkProgress,
                ct);

            // Whether the report accumulates from January is a property of the file itself, so it
            // is recorded here. The month's own figure is NOT computed here: deriving it needs the
            // previous month's document, and making one file's processing depend on another would
            // mean a file processed before its predecessor never picks the value up. It is derived
            // at read time instead — see CumulativePeriodService.DeriveMonthly.
            document.IsCumulative = CumulativePeriodService.DetectCumulative(
                document.OriginalFileName, ExtractAllMarkdown(rawJson));

            var analyticsJson = JsonSerializer.Serialize(result.Report);

            _db.AnalyticsExtractions.Add(new AnalyticsExtraction
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                AnalyticsJson = analyticsJson,
                SchemaVersion = "v1",
                CreatedDate = DateTime.UtcNow
            });

            // Coverage check against the rows actually detected in the source markdown. Some rows
            // are legitimately skipped as aggregate totals, so the threshold is generous — but a
            // large gap means the totals below cannot be trusted, and we say so rather than
            // marking the document plainly "Done" with numbers nobody double-checks.
            var warnings = result.Warnings.ToList();
            var detectedRows = CountDetectedTableRows(rawJson);

            // An empty result is never a valid outcome. This used to be reported as a clean
            // "Complete" with $0.00 totals — which reads as "this file genuinely has no sales"
            // rather than "nothing was extracted". Observed on a spreadsheet that Content
            // Understanding could not parse at all: it returned only the sheet name, and the
            // document was silently marked Done with zero lines.
            if (result.Report.Sales.Count == 0)
            {
                warnings.Add(detectedRows == 0
                    ? "Nothing was extracted: the document analysis returned no tabular content at all. " +
                      "The file may be in a format the extractor cannot read — open the 'Extracted' view to check."
                    : $"Nothing was extracted although ~{detectedRows} source rows were detected. The mapping step returned no lines.");
            }
            else if (detectedRows > 0 && result.Report.Sales.Count < detectedRows * 0.85)
            {
                warnings.Add($"Coverage: {result.Report.Sales.Count} canonical lines for ~{detectedRows} source rows detected.");
            }

            document.HasWarnings = warnings.Count > 0;
            document.ErrorMessage = warnings.Count > 0
                ? string.Join(" | ", warnings.Take(10))
                : null;

            if (document.HasWarnings)
            {
                _logger.LogWarning("Document {DocumentId} finished with {Count} warning(s): {Warnings}",
                    documentId, warnings.Count, document.ErrorMessage);
            }

            // Aggregates stored on the document row, computed once here. This is what lets
            // GET /api/documents avoid ever loading or parsing AnalyticsJson.
            document.ApplyAggregates(result.Report.Sales);

            document.ProcessingStatus = ProcessingStatus.Done;
            document.ProgressPct = 100;
            document.AnalysisCompletedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Processing cancelled for document {DocumentId}", documentId);
            // On ne touche plus à la base : le document a probablement été supprimé.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing failed for document {DocumentId}", documentId);
            try
            {
                var doc = await _db.Documents.FindAsync(new object[] { documentId }, CancellationToken.None);
                if (doc != null)
                {
                    doc.ProcessingStatus = ProcessingStatus.Failed;
                    doc.ErrorMessage = ex.Message;
                    doc.HasWarnings = true;
                    await _db.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch
            {
                // Le document a peut-être déjà été supprimé entre-temps — on l'ignore.
            }
        }
    }

    private static string ExtractAllMarkdown(string rawExtractionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawExtractionJson);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("contents", out var contents))
                return "";

            return string.Join("\n", contents.EnumerateArray()
                .Where(c => c.TryGetProperty("markdown", out _))
                .Select(c => c.GetProperty("markdown").GetString() ?? ""));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Counts data rows across ALL markdown tables. The previous version stopped at the first
    /// table of each content block, which understated the denominator and therefore hid exactly
    /// the shortfalls this check exists to catch.
    /// </summary>
    private static int CountDetectedTableRows(string rawExtractionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawExtractionJson);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("contents", out var contents))
                return 0;

            var total = 0;
            foreach (var content in contents.EnumerateArray())
            {
                if (!content.TryGetProperty("markdown", out var mdEl)) continue;
                var markdown = mdEl.GetString();
                if (string.IsNullOrWhiteSpace(markdown)) continue;

                // Count against the SAME normalized markdown the transformer works from, otherwise
                // the coverage check compares canonical lines to a row count that still includes
                // header noise and aggregate rows.
                var normalized = MarkdownTableNormalizer.Normalize(markdown).Markdown;

                total += AnalyticsTransformationService
                    .SplitTables(normalized)
                    .Sum(t => t.DataRows.Count);
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }
}
