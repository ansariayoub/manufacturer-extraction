using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Models;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// Handles year-to-date reports, whose totals accumulate from January instead of covering only
/// their own month.
///
/// The 3M "Western Maintenance - YTD 2-28-26 Detail Sales Report" is the reference case: its own
/// printed total is 1,406,580.58, but the figure the business compares against is 612,321.59 —
/// February alone. That number is simply not present in the file; it only exists as the difference
/// against the January report. Extracting the file perfectly can never produce it, so the
/// subtraction has to happen here.
///
/// Verified against the real 2026 files: January 794,258.99, February 1,406,580.58, and the
/// line-by-line delta computed below reproduces 612,321.59 exactly.
/// </summary>
public class CumulativePeriodService : ICumulativePeriodService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CumulativePeriodService> _logger;

    public CumulativePeriodService(AppDbContext db, ILogger<CumulativePeriodService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static readonly Regex YtdRegex = new(@"\bY\.?T\.?D\.?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A report is cumulative when it says so — either in its file name, or through a money column
    /// literally headed "YTD". "PYTD" (prior year to date) deliberately does not count on its own:
    /// it appears as a comparison column in reports that are otherwise monthly.
    /// </summary>
    public static bool DetectCumulative(string fileName, string markdown)
    {
        if (YtdRegex.IsMatch(fileName)) return true;

        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.TrimStart().StartsWith('|')) continue;

            var cells = line.Trim('|', ' ').Split('|').Select(c => c.Trim());
            if (cells.Any(c => c.Equals("YTD", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>The period immediately before the given one, or null if it cannot be parsed.</summary>
    public static (string Month, string Year)? PreviousPeriod(string? month, string? year)
    {
        if (!int.TryParse(month, out var m) || !int.TryParse(year, out var y)) return null;
        if (m is < 1 or > 12) return null;

        var pm = m == 1 ? 12 : m - 1;
        var py = m == 1 ? y - 1 : y;
        return (pm.ToString("00", CultureInfo.InvariantCulture), py.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>True when this period is the first of its year, so YTD already equals the month.</summary>
    public static bool IsFirstPeriodOfYear(string? month) =>
        int.TryParse(month, out var m) && m == 1;

    /// <summary>
    /// Derives a cumulative report's own-month total by subtracting the previous report's total.
    ///
    /// This is deliberately pure arithmetic over the aggregate columns that are already stored on
    /// each document — no canonical JSON is loaded, so it costs nothing on the polled listing.
    /// It is also exact: the month total is a sum over the union of line keys, and that sum always
    /// telescopes to the difference of the two document totals. Verified on the real 3M files,
    /// where 1,406,580.58 minus 794,258.99 gives 612,321.59 both ways.
    ///
    /// Doing it here rather than during processing keeps every file's processing self-contained:
    /// upload order no longer matters, and a month's figure appears as soon as both files exist.
    /// </summary>
    public static decimal? DeriveMonthly(
        bool isCumulative, string? month, decimal? ownTotal, decimal? previousTotal)
    {
        if (!isCumulative || ownTotal is null) return null;
        if (IsFirstPeriodOfYear(month)) return ownTotal;
        return previousTotal is null ? null : ownTotal - previousTotal;
    }

    /// <summary>
    /// Key used to pair a line with the same line in the previous month's report. It uses every
    /// identifying field the canonical schema carries — the source's own street address is not
    /// among them, but that granularity is not needed: the period total is a sum over the union of
    /// keys, so merging two lines under one key leaves the total unchanged. Confirmed on the real
    /// files, where four different key definitions all reproduced 612,321.59.
    /// </summary>
    private static string BuildKey(AnalyticsTransaction t)
    {
        static string N(string? s) => (s ?? "").Trim().ToUpperInvariant();

        return string.Join('|',
            N(t.CustomerId), N(t.CustomerName), N(t.City), N(t.State),
            N(t.ProductFamily), N(t.PartNo), N(t.PartDescription));
    }

    public async Task<CumulativeResult> ComputeMonthlyAsync(Document document, List<AnalyticsTransaction> currentLines, CancellationToken ct)
    {
        var previous = await FindPreviousAsync(document, ct);

        if (previous is null)
        {
            // January is its own year-to-date, so no subtraction is needed or possible.
            if (document.PeriodMonth?.TrimStart('0') == "1")
            {
                _logger.LogInformation("Document {Id} is the first period of the year — YTD equals the month", document.Id);
                return new CumulativeResult(currentLines, null);
            }

            return new CumulativeResult(null,
                $"This is a cumulative (YTD) report, so its total covers the whole year to date, not {document.PeriodMonth}/{document.PeriodYear} alone. " +
                "Upload the previous month's report for the same manufacturer and period to get the month's own figure.");
        }

        var previousLines = LoadLines(previous);
        if (previousLines.Count == 0)
        {
            return new CumulativeResult(null,
                $"The previous period's document ({previous.OriginalFileName}) holds no extracted lines, so the monthly figure could not be derived.");
        }

        var previousByKey = new Dictionary<string, (decimal Net, decimal Comm)>();
        foreach (var line in previousLines)
        {
            var key = BuildKey(line);
            previousByKey.TryGetValue(key, out var acc);
            previousByKey[key] = (acc.Net + (decimal)(line.NetSales ?? 0),
                                  acc.Comm + (decimal)(line.Commission ?? 0));
        }

        var currentByKey = new Dictionary<string, (decimal Net, decimal Comm, AnalyticsTransaction Sample)>();
        foreach (var line in currentLines)
        {
            var key = BuildKey(line);
            if (currentByKey.TryGetValue(key, out var acc))
            {
                currentByKey[key] = (acc.Net + (decimal)(line.NetSales ?? 0),
                                     acc.Comm + (decimal)(line.Commission ?? 0), acc.Sample);
            }
            else
            {
                currentByKey[key] = ((decimal)(line.NetSales ?? 0), (decimal)(line.Commission ?? 0), line);
            }
        }

        var previousSamples = previousLines
            .GroupBy(BuildKey)
            .ToDictionary(g => g.Key, g => g.First());

        // Iterate the UNION, not just the current month. Lines that were present last month and
        // have disappeared represent activity that was reversed, and dropping them silently
        // overstates the period — on the reference files that alone accounted for 27,693.64.
        var monthly = new List<AnalyticsTransaction>();
        var monthStart = ParseMonthStart(document);

        foreach (var key in currentByKey.Keys.Union(previousByKey.Keys))
        {
            currentByKey.TryGetValue(key, out var cur);
            previousByKey.TryGetValue(key, out var prev);

            var net = cur.Net - prev.Net;
            var comm = cur.Comm - prev.Comm;
            if (net == 0m && comm == 0m) continue;

            var template = cur.Sample ?? previousSamples[key];

            monthly.Add(new AnalyticsTransaction
            {
                SourceName = template.SourceName,
                Manufacturer = template.Manufacturer,
                CustomerId = template.CustomerId,
                CustomerName = template.CustomerName,
                City = template.City,
                State = template.State,
                ProductFamily = template.ProductFamily,
                PartNo = template.PartNo,
                PartDescription = template.PartDescription,
                Quantity = null, // quantities are not cumulative in these reports — do not invent one
                Date = monthStart,
                NetSales = (double)Math.Round(net, 2),
                Commission = (double)Math.Round(comm, 2)
            });
        }

        _logger.LogInformation(
            "Monthly delta for {Id}: {Lines} line(s), net {Net:0.00} (YTD {Ytd:0.00} minus {PrevFile})",
            document.Id, monthly.Count, monthly.Sum(m => m.NetSales ?? 0),
            currentLines.Sum(l => l.NetSales ?? 0), previous.OriginalFileName);

        return new CumulativeResult(monthly, null, previous.OriginalFileName);
    }

    private async Task<Document?> FindPreviousAsync(Document document, CancellationToken ct)
    {
        if (!int.TryParse(document.PeriodMonth, out var month) || !int.TryParse(document.PeriodYear, out var year))
            return null;

        var prevMonth = month == 1 ? 12 : month - 1;
        var prevYear = month == 1 ? year - 1 : year;

        var prevMonthText = prevMonth.ToString("00", CultureInfo.InvariantCulture);
        var prevYearText = prevYear.ToString(CultureInfo.InvariantCulture);

        return await _db.Documents
            .Include(d => d.AnalyticsExtraction)
            .Where(d => d.Id != document.Id
                     && d.Manufacturer == document.Manufacturer
                     && d.PeriodMonth == prevMonthText
                     && d.PeriodYear == prevYearText
                     && d.ProcessingStatus == ProcessingStatus.Done
                     && d.IsCumulative)
            .OrderByDescending(d => d.UploadDate)
            .FirstOrDefaultAsync(ct);
    }

    private static List<AnalyticsTransaction> LoadLines(Document document)
    {
        if (document.AnalyticsExtraction is null) return new();

        var report = JsonSerializer.Deserialize<AnalyticsReport>(
            document.AnalyticsExtraction.AnalyticsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return report?.Sales ?? new();
    }

    private static DateOnly? ParseMonthStart(Document document) =>
        int.TryParse(document.PeriodMonth, out var m) && int.TryParse(document.PeriodYear, out var y)
            && m is >= 1 and <= 12
                ? new DateOnly(y, m, 1)
                : null;
}
