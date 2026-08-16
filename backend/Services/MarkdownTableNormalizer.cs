using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// Cleans up the markdown Content Understanding produces from spreadsheets, before it ever
/// reaches the model.
///
/// Every transformation here is deterministic and exact. That matters: each one removes a task
/// the model was previously doing by hand, row by row, and getting wrong on a fraction of rows.
/// Observed on a real 1291-row commission report:
///
///  - Dates arrived as raw Excel serial numbers (46050). The model converted them itself and
///    drifted by anything from 5 to 25 days, so nearly every date in the output was wrong.
///  - Merged cells were emitted as duplicated columns ("CUST NO | CUST NO", "TOT ORD | TOT ORD",
///    "INV NO | INV NO | INV NO"), giving 31 columns of which half were repeats — ambiguity the
///    model had to resolve on every single row.
///  - The real header row was preceded by a page-furniture row ("Page 1 of 1") that happened to
///    be followed by the separator, so the parser treated the junk row as the header and the real
///    header — plus the report title rows — as data. Those title rows became empty phantom
///    records in the output.
///  - Floats carried binary noise (280.16000000000003) that the model had to transcribe verbatim.
/// </summary>
internal static class MarkdownTableNormalizer
{
    private static readonly Regex TableRowRegex = new(@"^\s*\|.*\|\s*$", RegexOptions.Compiled);
    private static readonly Regex SeparatorRegex = new(@"^\s*\|[\s:\-\|]+\|\s*$", RegexOptions.Compiled);

    // Header names that identify a date column. Deliberately narrow: we only rewrite values in
    // columns that clearly hold dates, so an invoice number that happens to look like a serial is
    // never touched.
    private static readonly Regex DateHeaderRegex = new(
        @"(^|\s|_)(DT|DATE|DATES)($|\s|_)|DATE", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Header wording that marks a column as a plausible-but-wrong alternative to a pinned "current
    // period" money column: a rolling/cumulative range, or a prior-year comparison.
    private static readonly Regex AmbiguousMoneyColumnRegex = new(
        @"\brange\b|\bprior\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TotalRowRegex = new(
        @"^\s*((grand|overall)\s+)?(total|totals|subtotal|sub-total|sum|result)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Excel's day zero. Excel wrongly treats 1900 as a leap year, which is why the epoch is the
    // 30th and not the 31st of December 1899.
    private static readonly DateTime ExcelEpoch = new(1899, 12, 30);

    // Serial range worth interpreting: roughly 1954 to 2079. Narrow enough that ordinary integers
    // (quantities, percentages, small ids) are never mistaken for dates.
    private const double MinSerial = 20_000;
    private const double MaxSerial = 65_000;

    public sealed record Result(string Markdown, int DroppedTotalRows, int DroppedColumns);

    public static Result Normalize(string markdown, string? moneyColumnPrefix = null)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder();
        var droppedTotals = 0;
        var droppedCols = 0;

        int i = 0;
        while (i < lines.Length)
        {
            if (!TableRowRegex.IsMatch(lines[i]))
            {
                output.AppendLine(lines[i]);
                i++;
                continue;
            }

            // Collect the whole run of consecutive table lines.
            var block = new List<string>();
            while (i < lines.Length && TableRowRegex.IsMatch(lines[i]))
            {
                block.Add(lines[i]);
                i++;
            }

            var (text, totals, cols) = NormalizeBlock(block, moneyColumnPrefix);
            output.Append(text);
            droppedTotals += totals;
            droppedCols += cols;
        }

        return new Result(output.ToString(), droppedTotals, droppedCols);
    }

    private static (string Text, int DroppedTotals, int DroppedColumns) NormalizeBlock(
        List<string> block, string? moneyColumnPrefix)
    {
        var grid = block
            .Where(l => !SeparatorRegex.IsMatch(l))
            .Select(SplitCells)
            .ToList();

        if (grid.Count == 0) return (string.Join("\n", block) + "\n", 0, 0);

        var width = grid.Max(r => r.Count);
        foreach (var row in grid)
            while (row.Count < width) row.Add("");

        // Banner rows are separated out FIRST, before any column analysis. A merged title cell
        // spans the full sheet width, so leaving these rows in would both mis-align the column
        // de-duplication statistics and give the header detector something to latch onto.
        var preamble = new List<string>();
        var body = new List<List<string>>();
        foreach (var row in grid)
        {
            var nonEmpty = row.Where(c => c.Length > 0).ToList();
            if (nonEmpty.Count == 0) continue;

            if (IsBannerRow(nonEmpty))
            {
                if (body.Count == 0)
                {
                    // Keep the longest variant: the title itself rather than a page marker or a
                    // print timestamp sharing the same row.
                    preamble.Add(nonEmpty
                        .GroupBy(v => v)
                        .OrderByDescending(g => g.Count())
                        .ThenByDescending(g => g.Key.Length)
                        .First().Key);
                }
                continue;
            }
            body.Add(row);
        }

        if (body.Count == 0)
        {
            var bannerOnly = new StringBuilder();
            foreach (var p in preamble) bannerOnly.AppendLine(p);
            return (bannerOnly.ToString(), 0, 0);
        }

        var keep = SelectColumns(body, width);
        var droppedColumns = width - keep.Count;
        body = body.Select(r => keep.Select(c => r[c]).ToList()).ToList();

        var headerIdx = FindHeaderRow(body);

        // Anything above the real header is page furniture or title text, not data.
        for (int r = 0; r < headerIdx; r++)
        {
            var text = string.Join(" ", body[r].Where(c => c.Length > 0).Distinct());
            if (text.Length > 0) preamble.Add(text);
        }

        var header = body[headerIdx];
        var dataRows = body.Skip(headerIdx + 1).ToList();

        // An operator can pin the exact netSales column by header prefix (e.g. when a report has
        // near-duplicate money columns — a "current month" figure next to a "current year range"
        // or "prior year range" figure that happen to read identically for some periods and
        // wildly differently for others). Removing the competing columns here, before the model
        // ever sees them, is what makes the choice exact regardless of chunk count — a text
        // instruction alone was observed to still drift on a ~1900-row, 15-chunk document.
        if (!string.IsNullOrWhiteSpace(moneyColumnPrefix))
        {
            var pinnedCol = Enumerable.Range(0, header.Count)
                .FirstOrDefault(c => header[c].StartsWith(moneyColumnPrefix, StringComparison.OrdinalIgnoreCase), -1);

            if (pinnedCol >= 0)
            {
                var dropCols = Enumerable.Range(0, header.Count)
                    .Where(c => c != pinnedCol && AmbiguousMoneyColumnRegex.IsMatch(header[c]))
                    .ToHashSet();

                if (dropCols.Count > 0)
                {
                    var survivors = Enumerable.Range(0, header.Count).Where(c => !dropCols.Contains(c)).ToList();
                    header = survivors.Select(c => header[c]).ToList();
                    dataRows = dataRows.Select(r => survivors.Select(c => r[c]).ToList()).ToList();
                    droppedColumns += dropCols.Count;
                }
            }
        }

        // Drop aggregate rows here rather than asking the model to recognise and skip them. This
        // also makes the "one row in, one row out" reconciliation exact.
        var droppedTotals = dataRows.Count(IsTotalRow);
        dataRows = dataRows.Where(r => !IsTotalRow(r)).ToList();

        // Footnote rows — a single populated cell in a wide table, e.g. the trailing
        // "Applied filters: Post Date is on or after ..." line these exports end with. They are
        // context, not transactions, so they move out of the table instead of becoming a record.
        var notes = new List<string>();
        if (header.Count >= 4)
        {
            foreach (var row in dataRows.Where(IsNoteRow))
                notes.Add(row.First(c => c.Length > 0));

            dataRows = dataRows.Where(r => !IsNoteRow(r)).ToList();
        }

        var dateColumns = Enumerable.Range(0, header.Count)
            .Where(c => DateHeaderRegex.IsMatch(header[c]))
            .ToHashSet();

        foreach (var row in dataRows)
            for (int c = 0; c < row.Count; c++)
                row[c] = CleanCell(row[c], dateColumns.Contains(c));

        var sb = new StringBuilder();
        foreach (var p in preamble) sb.AppendLine(p);
        if (preamble.Count > 0) sb.AppendLine();

        sb.AppendLine("| " + string.Join(" | ", header) + " |");
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", header.Count)));
        foreach (var row in dataRows)
            sb.AppendLine("| " + string.Join(" | ", row) + " |");

        foreach (var note in notes)
            sb.AppendLine().AppendLine(note);

        return (sb.ToString(), droppedTotals, droppedColumns);
    }

    private static List<string> SplitCells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed[1..];
        if (trimmed.EndsWith("|")) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    /// <summary>
    /// Chooses which columns survive: drops all-but-empty columns, and drops a column that simply
    /// repeats the one before it. Merged cells in the source spreadsheet are what produce those
    /// repeats, and they are pure noise — each one is another near-identical column the model has
    /// to disambiguate on every row.
    /// </summary>
    private static List<int> SelectColumns(List<List<string>> grid, int width)
    {
        var keep = new List<int>();

        for (int c = 0; c < width; c++)
        {
            var nonEmpty = grid.Count(r => r[c].Length > 0);
            if (nonEmpty == 0) continue;

            if (keep.Count > 0)
            {
                var prev = keep[^1];
                var compared = 0;
                var identical = 0;
                foreach (var row in grid)
                {
                    if (row[prev].Length == 0 && row[c].Length == 0) continue;
                    compared++;
                    if (row[prev] == row[c]) identical++;
                }

                if (compared > 0 && identical >= compared * 0.9) continue; // duplicate of previous
            }

            keep.Add(c);
        }

        return keep.Count > 0 ? keep : Enumerable.Range(0, width).ToList();
    }

    /// <summary>
    /// A banner row is a merged title, page marker or period caption stretched across the sheet:
    /// very few distinct values, each repeated many times. The test is deliberately tolerant of a
    /// second value, because these rows often carry a page number or a print timestamp alongside
    /// the title.
    /// </summary>
    private static bool IsBannerRow(List<string> nonEmpty)
    {
        if (nonEmpty.Count < 3) return false;

        var distinct = nonEmpty.Distinct().Count();
        var maxRepeat = nonEmpty.GroupBy(v => v).Max(g => g.Count());
        return distinct <= 3 && maxRepeat >= 3;
    }

    /// <summary>
    /// The header is the row that reads like labels rather than data. It is picked as the row with
    /// the most distinct labels among the first rows — not the first all-distinct row, because
    /// merged cells legitimately repeat a label ("INV NO | INV NO | INV NO") and requiring
    /// uniqueness rejected the real header on exactly the reports this class exists to fix.
    /// </summary>
    private static int FindHeaderRow(List<List<string>> body)
    {
        var best = -1;
        var bestScore = 0;

        for (int r = 0; r < Math.Min(body.Count, 25); r++)
        {
            var cells = body[r].Where(c => c.Length > 0).ToList();
            if (cells.Count < 4) continue;
            if (cells.Any(IsNumeric)) continue;

            var score = cells.Distinct().Count();
            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }

        return best >= 0 ? best : 0;
    }

    private static bool IsNoteRow(List<string> row) =>
        row.Count(c => c.Length > 0) == 1;

    /// <summary>
    /// A row is an aggregate — never a real transaction — if ANY of its cells reads like a total
    /// label, not just the first one. Multi-level pivot/SAP-style exports nest several tiers of
    /// subtotal (by product group, then customer, then division, then a grand total), and which
    /// column carries the label shifts depending on how many grouping levels are collapsed on that
    /// particular row — checking only the first cell missed most of them on a real 1738-row export
    /// (the "Result" marker for a customer-level subtotal sits in the product-group column, for
    /// example, while the row still starts with a real division code).
    /// </summary>
    private static bool IsTotalRow(List<string> row) =>
        row.Any(c => c.Length > 0 && TotalRowRegex.IsMatch(c));

    private static bool IsNumeric(string s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

    private static string CleanCell(string value, bool isDateColumn)
    {
        if (value.Length == 0) return value;
        if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return value;

        if (isDateColumn && d >= MinSerial && d <= MaxSerial)
        {
            // Exact conversion, done once here instead of 1291 times by the model.
            return ExcelEpoch.AddDays(Math.Truncate(d)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // Strip binary floating-point noise (280.16000000000003 -> 280.16) without touching
        // genuinely precise values: 4 decimals keeps rates like 0.0575 intact.
        if (value.Contains('.'))
        {
            var rounded = Math.Round(d, 4, MidpointRounding.AwayFromZero);
            return rounded.ToString("0.####", CultureInfo.InvariantCulture);
        }

        return value;
    }
}
