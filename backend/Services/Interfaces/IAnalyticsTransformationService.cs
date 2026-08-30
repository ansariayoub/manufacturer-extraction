using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Services.Interfaces;

/// <summary>
/// Outcome of a transformation, including everything the pipeline knows about how trustworthy
/// the numbers are. RowsSent/RowsReturned are the per-chunk reconciliation totals: a large gap
/// means rows were dropped somewhere, which is invisible if you only look at the final sum.
/// </summary>
public record TransformationResult(
    AnalyticsReport Report,
    IReadOnlyList<string> Warnings,
    int RowsSent,
    int RowsReturned,
    // True when a pinned money column (from custom instructions) was successfully matched against
    // at least one table's source rows and used to set or rescale its NetSales values. The
    // document's total is then verified against the source file directly, independent of whatever
    // row count the model happened to return — so the row-count-based coverage warning in
    // DocumentProcessingService, which otherwise reads as "data may be missing", would be a false
    // alarm here and is softened accordingly.
    bool MoneyColumnPinApplied = false);

public interface IAnalyticsTransformationService
{
    /// <summary>
    /// Transforms raw Content Understanding output into the canonical AnalyticsReport.
    /// The document is processed in row-chunks internally (see AnalyticsTransformationService) so
    /// large spreadsheets are no longer silently truncated to a single character budget.
    /// </summary>
    /// <param name="onProgress">
    /// Optional callback invoked with a value in [0,1] after each chunk completes, so the caller
    /// can update Document.ProgressPct during the "Mapping" phase instead of jumping straight
    /// from 60% to 100%.
    /// </param>
    Task<TransformationResult> TransformAsync(
        string rawExtractionJson,
        string? fallbackManufacturer,
        string? fallbackPeriodMonth,
        string? fallbackPeriodYear,
        string? customInstructions,
        Func<double, Task>? onProgress = null,
        CancellationToken ct = default);
}
