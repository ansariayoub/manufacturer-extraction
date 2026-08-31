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
    // period" money column: a rolling/cumulative range, a prior-year comparison, a SAP monthly
    // breakdown column (M01..M12, with the first one usually prefixed "ACT"), or a SAP fiscal-YTD
    // range like "ACT 01..06". Observed on real Geberit exports: one report carries six near-
    // identical monthly Sales/USD columns plus a cumulative one side by side, and — same lesson as
    // "range"/"prior" above — a text instruction alone was not enough to keep the model on the one
    // column asked for once the document spans enough chunks; the wrong columns must be physically
    // removed before the model ever sees them.
    // "ytd" catches a cumulative "YTD 2026"/"YTD 2025" column sitting right next to the report's
    // actual current-period figure (a plain "2026" header) — a real Uponor Sales export, where
    // summing the YTD column instead of the current-period one inflated later months by 2-4x
    // (Feb's true $2,956,333 vs a hallucinated ~$4.9M) since YTD only equals the period total in
    // the first month of the year, then keeps growing.
    private static readonly Regex AmbiguousMoneyColumnRegex = new(
        @"\brange\b|\bprior\b|\bytd\b|^py\b|^(act\s+)?m\d{2}\b|^act\s+\d{2}\.\.\d{2}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TotalRowRegex = new(
        // Second alternative catches the abbreviated form some reports pack into the SAME cell as
        // a code, e.g. "N1850  TOT" as a rep-level rollup sitting right next to that rep's grand
        // total — anchored at start doesn't help here since the code comes first, so this matches
        // "TOT" as a trailing whole word instead. Missing this let a real Ideal Sales report's
        // rep-total row survive as ordinary data and double-count the document's total outright.
        // "summary" catches a MotorScrubber/Western Sales export's leading "Grand Summary:" row,
        // which otherwise survives as a fake line item (its own "Total excl. shipping" cell equals
        // the document's real grand total, which is not a per-item sale).
        // Trailing "\btotal\b\s*$" (not just the abbreviated "tot") catches an Excel PivotTable's own
        // per-group subtotal label — "FERGUSON ENTERPRISES INC Total", "WINWHOLESALE INCORPORATED
        // Total" — sitting at the END of the customer/branch name rather than as its own word at the
        // start. A real Eemax export nests Customer > Branch > Postal Code with exactly this labeling
        // at the Customer/Branch levels; missing it let those rollup rows survive as if they were
        // leaf transactions and double-count everything under them.
        @"^\s*((grand|overall)\s+)?(total|totals|subtotal|sub-total|sum|result|summary)\b|\btot\b\s*$|\btotal\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches a cell that IS a grand-total row label and nothing else — "Grand Total", or Excel's
    // French pivot-table label "Total général" (the "." tolerates both a correctly-decoded é and
    // the "�" mojibake byte some of these exports arrive with). Deliberately narrower than
    // TotalRowRegex above: "Totals for Rep No 90" or "Subtotal" must NOT match here, because those
    // mark one of several subtotal rows inside an otherwise flat table (Bobrick), not the single
    // trailing aggregate of a hierarchical pivot (Eemax) — see LooksLikeHierarchicalPivot.
    private static readonly Regex GrandTotalRowRegex = new(
        @"^(grand total|total g.n.ral)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Excel's day zero. Excel wrongly treats 1900 as a leap year, which is why the epoch is the
    // 30th and not the 31st of December 1899.
    private static readonly DateTime ExcelEpoch = new(1899, 12, 30);

    // Serial range worth interpreting: roughly 1954 to 2079. Narrow enough that ordinary integers
    // (quantities, percentages, small ids) are never mistaken for dates.
    private const double MinSerial = 20_000;
    private const double MaxSerial = 65_000;

    // SpreadsheetExtractionService marks the start of each sheet's markdown with "# {sheetName}".
    // Tracking the most recent one lets a per-sheet pinned column apply to the right table when a
    // workbook's sheets each need a different money column (see perSheetMoneyColumnPrefixes below).
    private static readonly Regex SheetHeadingRegex = new(@"^#\s+(?<name>.+?)\s*$", RegexOptions.Compiled);

    public sealed record Result(string Markdown, int DroppedTotalRows, int DroppedColumns, bool HasCollapsedPivotRows);

    /// <summary>
    /// Keeps only the rows of one sheet's table whose named column matches Value exactly (case-
    /// insensitive, numeric-aware — "1" matches "1", "1.0" and "01"). Built for workbooks that mix
    /// several periods' worth of rows into one tab (a "month" column carrying stray entries from
    /// another month than the one the file is nominally for) — see the Kraus Sales BUILD sheet this
    /// was added for. Deliberately narrow (equality on one column, one sheet) rather than a general
    /// filter language, to keep the custom-instructions surface small and predictable.
    ///
    /// Value is null for the "is not blank" form instead: some reports (a Kutol Sales territory
    /// commission report) carry per-customer and grand-total subtotal rows with NO text label at
    /// all — every identifying column blank, only the numeric ones populated — so there is no
    /// keyword for IsTotalRow to key off. Those rows always leave a genuine per-item identifying
    /// column (here, "Item Code") blank, unlike every real line item, so filtering on that column
    /// being non-blank drops exactly the aggregate rows a text-based total detector cannot see.
    ///
    /// Values holds more than one entry for an "is X or Y or Z" clause — a Rheem Sales report
    /// tracks three separate manufacturer "factories" (Commercial/Residential/Tankless) out of the
    /// SAME uploaded file, each factory's total being the sum of several distinct "Product Line"
    /// category values (e.g. Commercial Factory = "Commercial Tank" + "COMMERCIAL TE" +
    /// "COMMERCIAL TG"), so a single-value equality filter can't express the split.
    /// </summary>
    public sealed record RowFilter(string Column, IReadOnlyList<string>? Values);

    public static Result Normalize(
        string markdown,
        string? moneyColumnPrefix = null,
        IReadOnlyDictionary<string, string>? perSheetMoneyColumnPrefixes = null,
        IReadOnlyDictionary<string, RowFilter>? perSheetRowFilters = null)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder();
        var droppedTotals = 0;
        var droppedCols = 0;
        var collapsedPivot = false;
        string? currentSheet = null;

        int i = 0;
        while (i < lines.Length)
        {
            if (!TableRowRegex.IsMatch(lines[i]))
            {
                var headingMatch = SheetHeadingRegex.Match(lines[i]);
                if (headingMatch.Success) currentSheet = headingMatch.Groups["name"].Value;

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

            var effectivePrefix = currentSheet is not null
                && perSheetMoneyColumnPrefixes is not null
                && perSheetMoneyColumnPrefixes.TryGetValue(currentSheet, out var pinned)
                ? pinned
                : moneyColumnPrefix;

            var rowFilter = currentSheet is not null
                && perSheetRowFilters is not null
                && perSheetRowFilters.TryGetValue(currentSheet, out var filter)
                ? filter
                : null;

            var (text, totals, cols, collapsed) = NormalizeBlock(block, effectivePrefix, rowFilter);
            output.Append(text);
            droppedTotals += totals;
            droppedCols += cols;
            collapsedPivot |= collapsed;
        }

        return new Result(output.ToString(), droppedTotals, droppedCols, collapsedPivot);
    }

    private static (string Text, int DroppedTotals, int DroppedColumns, bool CollapsedPivot) NormalizeBlock(
        List<string> block, string? moneyColumnPrefix, RowFilter? rowFilter = null)
    {
        var grid = block
            .Where(l => !SeparatorRegex.IsMatch(l))
            .Select(SplitCells)
            .ToList();

        if (grid.Count == 0) return (string.Join("\n", block) + "\n", 0, 0, false);

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

            // A single non-numeric value alone in an otherwise-blank row, appearing before the real
            // header is found, is a field-name preamble line — the vertical "one field per row"
            // block some SAP-style exports prepend (e.g. "Sold-to party", "Material group", ... one
            // per row, dozens of rows deep). Left in place these ate up FindHeaderRow's search
            // window and pushed the real header out of range, defaulting to row 0 (a banner) as the
            // "header" — which starved the whole document of any reliable column mapping.
            var isFieldNamePreambleLine =
                body.Count == 0 && nonEmpty.Count == 1 && !IsNumeric(nonEmpty[0]);

            // Bounded to body.Count == 0 — before the real header/data has been found — because
            // this heuristic (few distinct values, one repeated 3+ times) also matches perfectly
            // legitimate DATA rows once real rows are flowing: a branch with all-zero sales, or one
            // where several columns coincidentally hold the same figure (e.g. "current period" and
            // "$ change" being equal because the prior year was zero). Without this bound, a real
            // Uponor Sales export lost two entire branches this way — one all-zero, one a large
            // negative ("-16289" repeated across four columns) — silently, with no warning and no
            // effect on the reported total (their own sales figure was correct; only the roll-up-
            // by-branch row disappeared), which is exactly the kind of loss a document's row count
            // should have surfaced but the "Incomplete extraction" heuristic conveniently didn't
            // catch here since these are unrelated to netSales pinning.
            if ((body.Count == 0 && IsBannerRow(nonEmpty)) || isFieldNamePreambleLine)
            {
                if (body.Count == 0 && !isFieldNamePreambleLine)
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
            return (bannerOnly.ToString(), 0, 0, false);
        }

        // Found on the header's original (pre-selection) column layout, purely to let SelectColumns
        // tell a genuine merged-cell duplicate from two columns that only coincidentally hold equal
        // values — see the comment there.
        var headerRowForDedup = body[FindHeaderRow(body)];

        var keep = SelectColumns(body, width, headerRowForDedup);
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

        if (rowFilter is not null)
        {
            var filterCol = Enumerable.Range(0, header.Count)
                .FirstOrDefault(c => header[c].Equals(rowFilter.Column, StringComparison.OrdinalIgnoreCase), -1);

            if (filterCol >= 0)
            {
                dataRows = rowFilter.Values is null
                    ? dataRows.Where(r => r[filterCol].Trim().Length > 0).ToList()
                    : dataRows.Where(r => rowFilter.Values.Any(v => RowFilterValueMatches(r[filterCol], v))).ToList();
            }
        }

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

        // Hierarchical Excel PivotTable: customer > sub-account > order > invoice, where a parent
        // row's amount is exactly repeated by its own descendants (a customer with one order and
        // one line shows the same dollar figure three times, once per nesting level). There is no
        // reliable per-row signal distinguishing a "real" leaf row from a repeated parent total in
        // this shape — unlike Bobrick/SAP-style reports, none of the intermediate rows are labelled
        // "Total" — so summing the raw rows (or asking the model to) over- or under-counts by
        // 30-75%, observed on real Eemax exports. When the table also ends in an unambiguous
        // "Grand Total" / "Total général" row, trust that single figure — it is the workbook's own
        // correct aggregate — instead of the detail rows above it.
        var grandTotalIdx = FindGrandTotalRow(dataRows);
        var duplicateRowPivot = grandTotalIdx >= 0 && LooksLikeHierarchicalPivot(dataRows, grandTotalIdx);
        // A table can independently look like a "duplicate rows" pivot (Customer > Sub-account >
        // Order > Invoice, no reliable per-row way to tell a rollup from the one real leaf beneath
        // it — see LooksLikeHierarchicalPivot) AND ALSO have repeated period columns. When both fire
        // at once (observed on a real Eemax archive export), melting by period has no way to exclude
        // the ancestor rollups — they carry no "Total" label at any level — so it would count each
        // real transaction 3-5x over. Only take the melt path when the table is NOT also that shape;
        // duplicateRowPivot's own single-Grand-Total-row collapse below still gives the right total.
        var repeatedHeaderPivot = grandTotalIdx >= 0 && !duplicateRowPivot && HasRepeatedMoneyColumnHeaders(header, dataRows);
        var isHierarchicalPivot = repeatedHeaderPivot || duplicateRowPivot;

        int droppedTotals;
        var (meltedHeader, meltedRows, meltedDropped) = repeatedHeaderPivot
            ? MeltPeriodColumns(header, dataRows, grandTotalIdx)
            : (null, null, 0);

        if (meltedHeader is not null && meltedRows is not null)
        {
            // Every real (non-subtotal) row gets split into one row per period instead of collapsing
            // the whole table down to its Grand Total row — see MeltPeriodColumns. This keeps every
            // genuine customer/branch/leaf transaction as its own line, which the earlier single-row
            // collapse discarded entirely; only the workbook's own subtotal/rollup rows (matched the
            // same way ordinary total rows are, now including a trailing "<Name> Total" label) and
            // the Grand Total row itself are dropped, since both are redundant once every leaf row
            // is kept.
            droppedTotals = meltedDropped;
            header = meltedHeader;
            dataRows = meltedRows;
        }
        else if (isHierarchicalPivot)
        {
            droppedTotals = dataRows.Count - 1;

            // The model is separately instructed (base system prompt) to skip any row whose cells
            // read like an aggregate label — "Total", "Grand Total", etc. — which is exactly right
            // for the ordinary case but would make it silently discard the one row we just went out
            // of our way to keep. Relabel it to a plain entity name so it reads as real data instead
            // of a total to be skipped; the values themselves are untouched.
            var keptRow = dataRows[grandTotalIdx].ToList();
            for (int c = 0; c < keptRow.Count; c++)
                if (GrandTotalRowRegex.IsMatch(keptRow[c].Trim()))
                    keptRow[c] = "ALL CUSTOMERS COMBINED";

            dataRows = new List<List<string>> { keptRow };
        }
        else
        {
            // Drop aggregate rows here rather than asking the model to recognise and skip them.
            // This also makes the "one row in, one row out" reconciliation exact.
            droppedTotals = dataRows.Count(IsTotalRow);
            dataRows = dataRows.Where(r => !IsTotalRow(r)).ToList();

            // A second pass for the aggregate rows IsTotalRow can never see: some reports (Kutol
            // Sales, Wheeler Sales) bury a grand-total row with every identifying/label cell BLANK —
            // no "Total" keyword anywhere for a text-based scan to key off — sitting among otherwise
            // ordinary transaction rows. What gives it away is arithmetic, not text: its own value in
            // a money column exactly equals the sum of every OTHER row's value in that same column.
            // Both real cases needed a hand-written "only include rows where <column> is not blank"
            // instruction before this; this recovers the same result automatically.
            var beforeBlankAgg = dataRows.Count;
            dataRows = RemoveBlankLabeledAggregateRows(header, dataRows);
            droppedTotals += beforeBlankAgg - dataRows.Count;
        }

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

        return (sb.ToString(), droppedTotals, droppedColumns, isHierarchicalPivot);
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
    private static List<int> SelectColumns(List<List<string>> grid, int width, List<string>? headerRow = null)
    {
        var keep = new List<int>();

        for (int c = 0; c < width; c++)
        {
            var nonEmpty = grid.Count(r => r[c].Length > 0);
            if (nonEmpty == 0) continue;

            if (keep.Count > 0)
            {
                var prev = keep[^1];

                // A genuinely different field can still coincide with its neighbour on value for
                // most rows — e.g. a "PO_VALUE" (= unit price x quantity) column reads identical to
                // "NET_COST" (unit price) on every row where quantity happens to be 1, which can be
                // the overwhelming majority of rows on some real Kraus Sales exports. That is not a
                // merged cell repeating itself; it is two distinct fields agreeing by arithmetic
                // coincidence. Only fold two columns together when their OWN header text doesn't
                // already say they're different things — a real merged-cell continuation always
                // shares (or blanks out) its header, unlike this case.
                var headersDiffer = headerRow is not null
                    && headerRow[prev].Length > 0 && headerRow[c].Length > 0
                    && !headerRow[prev].Equals(headerRow[c], StringComparison.OrdinalIgnoreCase);

                if (!headersDiffer)
                {
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
        // Bound the search to BEFORE real data starts. Without this, a report with a mid-table
        // page break — a repeated column-header row after a page banner, common on paginated
        // exports — could out-score the real header (more distinct cells, e.g. an extra column
        // that only appears on later pages) and win outright, since the loop below picks the
        // single highest-scoring candidate anywhere in its window with no notion of "too late".
        // That silently discarded every real row above the repeated header as preamble — observed
        // losing roughly two-thirds of a real Ideal Sales report's total this way. A header can
        // only legitimately appear before the first row that already looks like data (several
        // numeric cells); once that line has been seen, any later header-shaped row is a repeat,
        // not the one that determines where data begins.
        var firstDataRowIdx = body.FindIndex(r => r.Count(IsNumeric) >= 2);
        var scanLimit = firstDataRowIdx >= 0 ? firstDataRowIdx : body.Count;

        var best = -1;
        var bestScore = 0;

        // 60, not 25: a defensive margin on top of the field-name-preamble filter above, in case a
        // report has more preamble lines than that filter recognises (extra banner rows, etc.).
        for (int r = 0; r < Math.Min(scanLimit, 60); r++)
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

    /// <summary>
    /// Finds and drops a grand-total row that carries NO text label at all — every identifying
    /// column blank, only the numeric ones populated, so <see cref="IsTotalRow"/> has nothing to
    /// match. What gives it away instead is arithmetic: its value in a money column exactly equals
    /// the sum of every OTHER row's value in that same column. Two real reports needed this — a
    /// Kutol Sales territory commission report and a Wheeler Sales file that buried an unlabeled
    /// total mid-table, doubling the reported total — both previously only fixable with a hand-
    /// written "only include rows where &lt;column&gt; is not blank" instruction.
    ///
    /// Deliberately conservative to avoid dropping a real transaction that happens to have blank
    /// identifying fields for a legitimate reason (a walk-in/cash sale with no customer name, say):
    /// requires at least 3 other rows to compare against, requires EVERY "label-like" column (one
    /// where most rows hold non-numeric text) to be blank on the candidate row, and requires the
    /// reconciliation to hold on at least half of the table's populated numeric columns at once —
    /// a coincidence across several independent dollar figures simultaneously is not realistic.
    /// </summary>
    private static List<List<string>> RemoveBlankLabeledAggregateRows(List<string> header, List<List<string>> dataRows)
    {
        if (dataRows.Count < 4) return dataRows;

        var width = header.Count;
        var populatedCounts = Enumerable.Range(0, width)
            .Select(c => dataRows.Count(r => c < r.Count && r[c].Length > 0))
            .ToList();

        var labelCols = Enumerable.Range(0, width)
            .Where(c => populatedCounts[c] >= dataRows.Count * 0.3 &&
                        dataRows.Count(r => c < r.Count && r[c].Length > 0 && !IsNumeric(r[c])) >= populatedCounts[c] * 0.5)
            .ToList();
        if (labelCols.Count == 0) return dataRows;

        var numericCols = Enumerable.Range(0, width)
            .Where(c => dataRows.Count(r => c < r.Count && IsNumeric(r[c])) >= dataRows.Count * 0.5)
            .ToList();
        if (numericCols.Count == 0) return dataRows;

        var columnSums = numericCols.ToDictionary(c => c, c => dataRows.Sum(r =>
            c < r.Count && IsNumeric(r[c]) ? double.Parse(r[c], NumberStyles.Any, CultureInfo.InvariantCulture) : 0));

        bool IsBlankAcrossLabels(List<string> row) => labelCols.All(c => c >= row.Count || row[c].Length == 0);

        bool ReconcilesAsAggregate(List<string> row)
        {
            var populated = numericCols.Where(c => c < row.Count && IsNumeric(row[c])).ToList();
            if (populated.Count == 0) return false;

            var matches = populated.Count(c =>
            {
                var val = double.Parse(row[c], NumberStyles.Any, CultureInfo.InvariantCulture);
                if (Math.Abs(val) < 0.01) return false; // a trivial zero match carries no evidence
                var tolerance = Math.Max(0.02, Math.Abs(val) * 0.001);
                return Math.Abs(columnSums[c] - 2 * val) < tolerance;
            });

            return matches > 0 && matches >= populated.Count / 2.0;
        }

        return dataRows.Where(r => !(IsBlankAcrossLabels(r) && ReconcilesAsAggregate(r))).ToList();
    }

    /// <summary>Index of the single unambiguous "Grand Total"/"Total général" row, or -1.</summary>
    private static int FindGrandTotalRow(List<List<string>> dataRows)
    {
        for (int r = 0; r < dataRows.Count; r++)
            if (dataRows[r].Any(c => GrandTotalRowRegex.IsMatch(c.Trim())))
                return r;
        return -1;
    }

    /// <summary>
    /// True when a large share of the OTHER rows (excluding the grand total row itself) share an
    /// exact numeric fingerprint with some other row — the signature of a PivotTable outline, where
    /// a parent row's total is repeated verbatim by each of its descendants down to the leaf. Real
    /// transaction rows essentially never coincide on every money column at once across 30%+ of a
    /// multi-hundred-row table, so this only fires on the shape it is meant for.
    /// </summary>
    private static bool LooksLikeHierarchicalPivot(List<List<string>> dataRows, int grandTotalIdx)
    {
        var others = dataRows.Where((_, i) => i != grandTotalIdx).ToList();
        if (others.Count < 5) return false;

        var width = others[0].Count;
        var numericColumns = Enumerable.Range(0, width)
            .Where(c => others.Count(r => IsNumeric(r[c])) >= others.Count * 0.5)
            .ToList();
        if (numericColumns.Count == 0) return false;

        var seen = new HashSet<string>();
        var duplicates = 0;
        var nonBlank = 0;
        foreach (var row in others)
        {
            var values = numericColumns.Select(c => row[c]).ToList();
            if (values.All(v => v.Length == 0)) continue;

            nonBlank++;
            var key = string.Join("|", values);
            if (!seen.Add(key)) duplicates++;
        }

        return nonBlank > 0 && duplicates >= nonBlank * 0.3;
    }

    private static bool IsNumeric(string s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// True when the SAME money-like header text (e.g. "Net Sales") appears 2+ times across mostly-
    /// numeric columns — the signature of an Excel PivotTable laid out with one column-group per
    /// period side by side (Jan/Feb/Mar.../Net Sales, Commission repeated per month) rather than one
    /// row per transaction. A real Eemax export in this shape has no single column that already sums
    /// every period, so pinning one named column (the ordinary fix for an ambiguous-but-singular
    /// money column) can't recover the right total, and summing every leaf row double-counts because
    /// these reports also nest customer > sub-account subtotal rows with no reliable "Total" label at
    /// that level. The one number the workbook itself vouches for is its own trailing "Grand
    /// Total"/"Total général" row, spanning all the period columns at once — so this is treated the
    /// same as <see cref="LooksLikeHierarchicalPivot"/>: collapse the whole table down to that single
    /// row instead of trusting either the model's row-by-row read or a naive column sum.
    /// </summary>
    private static bool HasRepeatedMoneyColumnHeaders(List<string> header, List<List<string>> dataRows)
    {
        if (dataRows.Count < 5) return false;

        var numericCols = Enumerable.Range(0, header.Count)
            .Where(c => dataRows.Count(r => c < r.Count && IsNumeric(r[c])) >= dataRows.Count * 0.3)
            .ToHashSet();
        if (numericCols.Count == 0) return false;

        return header
            .Select((h, c) => (h, c))
            .Where(x => x.h.Length > 0 && numericCols.Contains(x.c))
            .GroupBy(x => x.h, StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Count() >= 2);
    }

    /// <summary>
    /// Turns a table with several repeated period column-groups (see
    /// <see cref="HasRepeatedMoneyColumnHeaders"/>) — e.g. "Jan | Jan | Feb | Feb" over "Net Sales |
    /// Commission | Net Sales | Commission" — into one row PER (real row × period) pair: a customer
    /// with Jan and Feb figures becomes two rows, "&lt;customer&gt; | Jan | &lt;netSales&gt; |
    /// &lt;commission&gt;" and "&lt;customer&gt; | Feb | &lt;netSales&gt; | &lt;commission&gt;". This
    /// keeps every genuine leaf row as its own transaction — unlike collapsing straight to the Grand
    /// Total row, which is correct for the total but throws every real customer/branch line away.
    ///
    /// Columns whose top header starts with "Total" are dropped rather than turned into their own
    /// period, since they already sum every other period and melting them too would double the
    /// total. Rows matching <see cref="IsTotalRow"/> (which now also catches a PivotTable's own
    /// "&lt;Name&gt; Total" subtotal label, not just a leading "Total") are excluded entirely — they
    /// are redundant rollups once every leaf row survives, not real transactions.
    /// </summary>
    private static (List<string>? Header, List<List<string>>? Rows, int Dropped) MeltPeriodColumns(
        List<string> header, List<List<string>> dataRows, int grandTotalIdx)
    {
        var grandRow = dataRows[grandTotalIdx];

        var groups = new List<(int Start, int Len, string Label)>();
        for (int c = 0; c < header.Count;)
        {
            var label = header[c];
            var start = c;
            while (c < header.Count && header[c] == label) c++;
            groups.Add((start, c - start, label));
        }

        var periodGroups = groups
            .Where(g => g.Label.Length > 0 && !g.Label.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
            .Select(g => (g.Start, g.Len))
            .ToList();
        var headerIsPeriodRow = true;

        // A real period grouping is 2+ columns wide (a money column and at least a commission
        // column repeated per period, as in every case this was built for). Some pivot exports have
        // an extra row-label prefix (Customer Name/Branch/Postal Code) that shifts which row
        // FindHeaderRow lands on, so `header` here is sometimes the "Net Sales/Commission" sub-type
        // row instead of the "Jan/Feb/Mar" period row — every group in that shape is 1 column wide
        // (the labels alternate, none repeat consecutively), which would silently mint one fake
        // "period" per money/commission column instead of one per real period.
        if (periodGroups.Count == 0 || periodGroups.All(g => g.Len < 2))
        {
            // The true period row (the one with "Jan"/"Feb"/"Mar"...) was flattened into unstructured
            // preamble text by the time this code runs, so its column boundaries can't be recovered
            // from text. But the Grand Total row still carries the shape: label-only prefix columns
            // (Customer Name, Branch, Postal Code) are always blank on that row, while every real
            // period's money/commission columns are populated — so grouping by "does the Grand Total
            // row have a value here" recovers the same column runs without needing the label.
            var numericGroups = new List<(int Start, int Len)>();
            for (int c = 0; c < grandRow.Count;)
            {
                if (c >= header.Count || !IsNumeric(grandRow[c])) { c++; continue; }
                var start = c;
                while (c < grandRow.Count && IsNumeric(grandRow[c])) c++;
                numericGroups.Add((start, c - start));
            }

            // A pivot with no blank separator between periods (every money/commission column packed
            // back to back, as opposed to a blank gap between periods) collapses to ONE numeric run
            // rather than several — but the sub-column labels underneath ("Net Sales", "Commissions",
            // "Net Sales", "Commissions", ...) still cycle once per period. Find that cycle length
            // (how many columns until the first label repeats) and slice the single run into
            // equal-width periods by it, so this reduces to the same shape as the multi-run case.
            if (numericGroups.Count == 1)
            {
                var (runStart, runLen) = numericGroups[0];
                var firstLabel = runStart < header.Count ? header[runStart] : "";
                var cycleLen = Enumerable.Range(1, runLen - 1)
                    .FirstOrDefault(k => runStart + k < header.Count && header[runStart + k] == firstLabel, 0);

                if (cycleLen > 0 && runLen % cycleLen == 0 && runLen / cycleLen >= 2)
                {
                    numericGroups = Enumerable.Range(0, runLen / cycleLen)
                        .Select(i => (Start: runStart + i * cycleLen, Len: cycleLen))
                        .ToList();
                }
            }

            // Only trust this when every run is the same width and there is more than one — a
            // single accidental run (an ordinary total row with no periods at all) must not be
            // reinterpreted as "one period".
            if (numericGroups.Count >= 2 && numericGroups.Select(g => g.Len).Distinct().Count() == 1)
            {
                periodGroups = numericGroups;
                headerIsPeriodRow = false;
            }
            else
            {
                return (null, null, 0);
            }
        }

        var subHeaderRow = dataRows.FirstOrDefault(r =>
            r != grandRow &&
            r.Count(c => c.Length > 0) > 0 &&
            r.Count(c => c.Length > 0 && !IsNumeric(c)) >= r.Count(c => c.Length > 0) * 0.5);

        var width = periodGroups[0].Len;
        var subHeaderNames = Enumerable.Range(0, width)
            .Select(k =>
            {
                var col = periodGroups[0].Start + k;
                // When `header` is the real period row (Jan/Jan/Feb/Feb), the sub-column names (Net
                // Sales/Commission) live in `subHeaderRow` instead. When the numeric-run fallback
                // fired, `header` already IS that Net Sales/Commission row, so use it directly.
                var name = headerIsPeriodRow
                    ? (subHeaderRow is not null && col < subHeaderRow.Count ? subHeaderRow[col] : "")
                    : (col < header.Count ? header[col] : "");
                return name.Length > 0 ? name : $"Value{k + 1}";
            })
            .ToList();

        var periodLabels = Enumerable.Range(0, periodGroups.Count)
            .Select(i => headerIsPeriodRow && header[periodGroups[i].Start].Length > 0 && header[periodGroups[i].Start].Length < 20
                ? header[periodGroups[i].Start]
                : $"Period {i + 1}")
            .ToList();

        var identityEnd = periodGroups.Min(g => g.Start);
        var dropped = 0;
        var newRows = new List<List<string>>();

        for (int r = 0; r < dataRows.Count; r++)
        {
            if (r == grandTotalIdx || row_is_subheader(dataRows[r], subHeaderRow) || IsTotalRow(dataRows[r]))
            {
                dropped++;
                continue;
            }

            var row = dataRows[r];
            var identity = string.Join(" ", Enumerable.Range(0, Math.Min(identityEnd, row.Count))
                .Select(c => row[c])
                .Where(v => v.Length > 0));
            if (identity.Length == 0) identity = "(unlabeled)";

            var contributedAny = false;
            for (int g = 0; g < periodGroups.Count; g++)
            {
                var (start, len) = periodGroups[g];
                var vals = Enumerable.Range(start, len).Select(c => c < row.Count ? row[c] : "").ToList();
                if (vals.All(v => v.Length == 0)) continue;

                contributedAny = true;
                newRows.Add(new List<string> { identity, periodLabels[g] }.Concat(vals).ToList());
            }

            if (!contributedAny) dropped++;
        }

        if (newRows.Count == 0) return (null, null, 0);

        var newHeader = new List<string> { "Customer", "Period" }.Concat(subHeaderNames).ToList();
        return (newHeader, newRows, dropped);

        static bool row_is_subheader(List<string> row, List<string>? subHeaderRow) => row == subHeaderRow;
    }

    /// <summary>
    /// Equality for a row-filter comparison: numeric-aware first (so "1", "1.0" and "01" all match
    /// each other, since Excel and the operator's typed instruction rarely agree on formatting),
    /// falling back to a plain case-insensitive string match for non-numeric columns.
    /// </summary>
    private static bool RowFilterValueMatches(string cell, string target)
    {
        if (double.TryParse(cell, NumberStyles.Any, CultureInfo.InvariantCulture, out var cellNum)
            && double.TryParse(target, NumberStyles.Any, CultureInfo.InvariantCulture, out var targetNum))
        {
            return Math.Abs(cellNum - targetNum) < 0.0001;
        }

        return cell.Trim().Equals(target.Trim(), StringComparison.OrdinalIgnoreCase);
    }

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
