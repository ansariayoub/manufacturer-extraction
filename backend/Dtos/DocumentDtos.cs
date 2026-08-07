using System.Text.Json;
using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Dtos;

public record DocumentSummaryDto(
    Guid Id, string FileName, long FileSizeBytes, DateTime UploadedAt,
    string Manufacturer, string PeriodMonth, string PeriodYear,
    string Status, double ProgressPct, string? ErrorMessage, bool HasWarnings,
    decimal? TotalNetSales, decimal? TotalCommission,
    int? LineCount, int? CustomerCount, string? CustomInstructions,
    bool IsCumulative, decimal? MonthlyNetSales, decimal? MonthlyCommission, int? MonthlyLineCount
);

/// <summary>
/// Ultra-light row used by the polling loop (GET /api/documents/status). Deliberately holds
/// nothing but the fields that actually change while a document is being processed, so the
/// query can be answered from a handful of small columns.
/// </summary>
public record DocumentStatusDto(
    Guid Id, string Status, double ProgressPct, string? ErrorMessage, bool HasWarnings
);

public record DocumentDetailDto(
    Guid Id, string FileName, long FileSizeBytes, DateTime UploadedAt,
    string Manufacturer, string PeriodMonth, string PeriodYear,
    string Status, double ProgressPct, string? ErrorMessage, bool HasWarnings,
    decimal? TotalNetSales, decimal? TotalCommission,
    int? LineCount, int? CustomerCount, string? CustomInstructions,
    bool IsCumulative, decimal? MonthlyNetSales, decimal? MonthlyCommission, int? MonthlyLineCount,
    string? RawExtractionJson, List<AnalyticsTransaction> CanonicalRecords, string? SourceUrl
);

public record ReanalyzeRequest(string? CustomInstructions);

public static class DocumentMappingExtensions
{
    /// <summary>
    /// Builds the queue-table row from the aggregate columns stored on the Document itself.
    /// This never touches AnalyticsExtraction: that navigation holds an nvarchar(max) blob that
    /// can run to several megabytes per file, and deserializing it here — once per document, on
    /// every poll tick — was what made the queue take many seconds to appear.
    /// The aggregates are computed once, at the end of processing, in DocumentProcessingService.
    /// </summary>
    public static DocumentSummaryDto ToSummaryDto(this Document d) =>
        new(d.Id, d.OriginalFileName, d.FileSizeBytes, d.UploadDate,
            d.Manufacturer, d.PeriodMonth, d.PeriodYear,
            d.ProcessingStatus.ToString(), d.ProgressPct, d.ErrorMessage, d.HasWarnings,
            d.TotalNetSales, d.TotalCommission,
            d.LineCount, d.CustomerCount, d.CustomInstructions,
            // The month's own figures are derived at read time, not stored — see
            // CumulativePeriodService.DeriveMonthly. Callers that need them fill them in.
            d.IsCumulative, null, null, null);

    /// <summary>
    /// Detail view for a single document — here deserializing the canonical JSON is fine and
    /// expected, because it only happens when the user actually opens the viewer for one file.
    /// </summary>
    public static DocumentDetailDto ToDetailDto(this Document d, string? sourceUrl)
    {
        var summary = d.ToSummaryDto();

        List<AnalyticsTransaction> records = new();
        if (d.AnalyticsExtraction is not null)
        {
            var report = JsonSerializer.Deserialize<AnalyticsReport>(
                d.AnalyticsExtraction.AnalyticsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // For a cumulative report the month's own lines are the ones worth looking at, so the
            // viewer shows those when they exist and falls back to the raw cumulative lines.
            records = report?.MonthlySales ?? report?.Sales ?? new();
        }

        return new DocumentDetailDto(
            summary.Id, summary.FileName, summary.FileSizeBytes, summary.UploadedAt,
            summary.Manufacturer, summary.PeriodMonth, summary.PeriodYear,
            summary.Status, summary.ProgressPct, summary.ErrorMessage, summary.HasWarnings,
            summary.TotalNetSales, summary.TotalCommission,
            summary.LineCount, summary.CustomerCount, summary.CustomInstructions,
            summary.IsCumulative, summary.MonthlyNetSales, summary.MonthlyCommission, summary.MonthlyLineCount,
            d.RawExtraction?.RawJson, records, sourceUrl);
    }
}
