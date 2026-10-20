using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Dtos;

public record DocumentSummaryDto(
    Guid Id, string FileName, long FileSizeBytes, DateTime UploadedAt,
    string Manufacturer, string PeriodMonth, string PeriodYear,
    string Status, double ProgressPct, string? ErrorMessage, bool HasWarnings,
    decimal? TotalNetSales, decimal? TotalCommission,
    int? LineCount, int? CustomerCount, string? CustomInstructions,
    bool IsCumulative, decimal? MonthlyNetSales, decimal? MonthlyCommission, int? MonthlyLineCount,
    string ClientName
);

/// <summary>
/// Ultra-light row used by the polling loop (GET /api/documents/status). Deliberately holds
/// nothing but the fields that actually change while a document is being processed, so the
/// query can be answered from a handful of small columns.
/// </summary>
public record DocumentStatusDto(
    Guid Id, string Status, double ProgressPct, string? ErrorMessage, bool HasWarnings
);

/// <summary>
/// Header-level view of one document, opened instantly by the viewer. The heavy content (the raw
/// extraction and the canonical lines) is NOT part of it any more: each viewer tab fetches only its
/// own slice, one page at a time — see DocumentContentController.
/// </summary>
public record DocumentDetailDto(
    Guid Id, string FileName, long FileSizeBytes, DateTime UploadedAt,
    string Manufacturer, string PeriodMonth, string PeriodYear,
    string Status, double ProgressPct, string? ErrorMessage, bool HasWarnings,
    decimal? TotalNetSales, decimal? TotalCommission,
    int? LineCount, int? CustomerCount, string? CustomInstructions,
    bool IsCumulative, decimal? MonthlyNetSales, decimal? MonthlyCommission, int? MonthlyLineCount,
    string ClientName,
    string? SourceUrl
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
    public static DocumentSummaryDto ToSummaryDto(this Document d, string clientName) =>
        new(d.Id, d.OriginalFileName, d.FileSizeBytes, d.UploadDate,
            d.Manufacturer, d.PeriodMonth, d.PeriodYear,
            d.ProcessingStatus.ToString(), d.ProgressPct, d.ErrorMessage, d.HasWarnings,
            d.TotalNetSales, d.TotalCommission,
            d.LineCount, d.CustomerCount, d.CustomInstructions,
            // The month's own figures are derived at read time, not stored — see
            // CumulativePeriodService.DeriveMonthly. Callers that need them fill them in.
            d.IsCumulative, null, null, null,
            clientName);

    public static DocumentDetailDto ToDetailDto(this Document d, string clientName, string? sourceUrl)
    {
        var s = d.ToSummaryDto(clientName);
        return new DocumentDetailDto(
            s.Id, s.FileName, s.FileSizeBytes, s.UploadedAt,
            s.Manufacturer, s.PeriodMonth, s.PeriodYear,
            s.Status, s.ProgressPct, s.ErrorMessage, s.HasWarnings,
            s.TotalNetSales, s.TotalCommission,
            s.LineCount, s.CustomerCount, s.CustomInstructions,
            s.IsCumulative, s.MonthlyNetSales, s.MonthlyCommission, s.MonthlyLineCount,
            s.ClientName, sourceUrl);
    }
}

// --- Paged viewer content ------------------------------------------------------------------

public record CanonicalPageDto(
    List<AnalyticsTransaction> Records, int Page, int PageSize, int TotalRecords,
    decimal TotalNetSales, decimal TotalCommission);

/// <summary>One slice of the pretty-printed raw extraction, cut by characters so a payload that is
/// a single enormous line still pages.</summary>
public record ExtractedPageDto(string Text, int Page, int PageCount, int TotalChars);

public record SourceSheetInfoDto(string Name, int RowCount);

public record SourcePageDto(
    List<SourceSheetInfoDto> Sheets, int SheetIndex, List<string> Headers, List<List<string>> Rows,
    int Page, int PageSize, int TotalRows);
