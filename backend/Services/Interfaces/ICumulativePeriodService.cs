using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Services.Interfaces;

/// <summary>
/// Outcome of deriving a month's own activity from a year-to-date report.
/// <paramref name="MonthlyLines"/> is null when the derivation was not possible, in which case
/// <paramref name="Warning"/> explains why and the document keeps only its cumulative total.
/// </summary>
public record CumulativeResult(
    List<AnalyticsTransaction>? MonthlyLines,
    string? Warning,
    string? PreviousFileName = null);

public interface ICumulativePeriodService
{
    Task<CumulativeResult> ComputeMonthlyAsync(
        Document document, List<AnalyticsTransaction> currentLines, CancellationToken ct);
}
