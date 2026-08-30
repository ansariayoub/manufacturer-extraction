using System.Collections.Concurrent;
using System.ClientModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using Azure;
using OpenAI.Chat;
using ManufacturerExtraction.Api.Models;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Services;

public class AnalyticsTransformationService : IAnalyticsTransformationService
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<AnalyticsTransformationService> _logger;

    // How many table rows we try to fit in a single model call. This is what replaces the old
    // hard 180,000-character cutoff: instead of truncating the document, we now process it in
    // full, just split across several calls.
    //
    // Lowered from 200: tables landing just under the old limit (188 rows, 199 rows — big enough to
    // stay in ONE call, not big enough to force a split) were repeatedly observed fabricating or
    // dropping a handful of rows within that single call, on real DURAVIT files, across multiple
    // reanalyze attempts. Smaller sheets (under ~100 rows) never showed this. Forcing a split well
    // before 200 trades a few more model calls for the reliability the deterministic netSales
    // override depends on — it only engages when a table's row count matches exactly.
    private const int TargetRowsPerChunk = 90;

    // Floor for the split-on-truncation recursion. Below this many rows we stop splitting and
    // record a warning instead of looping forever.
    private const int MinRowsToSplit = 4;

    // Retries per model call for transient Azure failures (429 throttling above all).
    private const int MaxTransientRetries = 5;

    // Sheet-level context (report title, period, commission rates) repeated in every chunk.
    private const int MaxPreambleChars = 2_000;

    private readonly OpenAiConcurrencyLimiter _limiter;

    public AnalyticsTransformationService(
        IConfiguration config, ILogger<AnalyticsTransformationService> logger, OpenAiConcurrencyLimiter limiter)
    {
        _logger = logger;
        _limiter = limiter;

        var endpoint = config["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("Azure OpenAI endpoint missing");
        var apiKey = config["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("Azure OpenAI API key missing");
        var deployment = config["AzureOpenAI:DeploymentName"]
            ?? throw new InvalidOperationException("Azure OpenAI deployment name missing");

        var clientOptions = new AzureOpenAIClientOptions
        {
            // Lowered from 10 minutes: the SDK's own ClientRetryPolicy retries a stalled HTTP call
            // internally (a few attempts) before giving up, so a 10-minute budget per attempt meant
            // a single chunk could burn up to ~40 minutes before our own retry loop below ever saw
            // the failure. 5 minutes is still generous for one chunk (capped at 90 rows) and lets
            // failures surface — and get retried with backoff — much sooner.
            NetworkTimeout = TimeSpan.FromMinutes(5)
        };

        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey), clientOptions);
        _chatClient = azureClient.GetChatClient(deployment);
    }

    private static readonly BinaryData Schema = BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "sales": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "sourceName": { "type": ["string", "null"] },
              "manufacturer": { "type": ["string", "null"] },
              "customerId": { "type": ["string", "null"] },
              "customerName": { "type": ["string", "null"] },
              "date": { "type": ["string", "null"], "description": "ISO 8601 format yyyy-MM-dd" },
              "city": { "type": ["string", "null"] },
              "state": { "type": ["string", "null"] },
              "productFamily": { "type": ["string", "null"] },
              "partNo": { "type": ["string", "null"] },
              "partDescription": { "type": ["string", "null"] },
              "quantity": { "type": ["number", "null"] },
              "netSales": { "type": ["number", "null"] },
              "commission": { "type": ["number", "null"] }
            },
            "required": ["sourceName","manufacturer","customerId","customerName","date","city","state","productFamily","partNo","partDescription","quantity","netSales","commission"],
            "additionalProperties": false
          }
        }
      },
      "required": ["sales"],
      "additionalProperties": false
    }
    """);

    private static readonly string BaseSystemPrompt = """
        You transform raw document-extraction JSON from manufacturer sales, commission, or rebate reports into
        a canonical "sales" JSON array matching a fixed database schema. Rules:
        - Extract every itemized row that represents an individual transaction, adjustment, or rebate tied to a specific entity (distributor, dealer, product, or customer name) — even if the document is a commission statement, rebate summary, or adjustment report rather than a classic invoice.
        - Only skip a row if it is a pure aggregate that sums OTHER rows already extracted. If a row has a distinct entity name, extract it even if the document also contains totals elsewhere.
        - If the document contains a commission or deduction figure that applies to the whole document or period rather than to a single entity, and it cannot be reliably split across the individual entity rows, add ONE additional row for it: set "customerName" to "Unallocated Commission Adjustment", set "commission" to that value, leave other fields null except sourceName, manufacturer, and date.
        - For commission/rebate documents: map the distributor or entity name to "customerName", the adjustment amount to "netSales", and leave fields that do not apply as null.
        - PIVOT-STYLE REPORTS: some reports have one row per customer/category with SEVERAL money columns for different periods side by side (e.g. current month, same month last year, year-to-date this year, year-to-date last year, rolling 12 months, dollar/percent difference) instead of one row per dated transaction. When you see this shape:
        - Pick exactly ONE of those money columns as "netSales" for that row: the one whose header names the CURRENT period being processed (see the fallback period given below — month/year). Do not use prior-year, rolling-total, or difference columns as "netSales", and do not create separate rows for the other period columns.
        - There is usually no per-row date, quantity, part number, or commission in this report shape — leave those fields null rather than guessing.
        - If none of the money columns explicitly names the requested period (e.g. columns are just "YTD", "PYTD", "$ Growth", "Growth %", "PY Total" with no month/year in the header), do NOT return an empty result. Instead, use the column representing the current cumulative total (typically labeled "YTD" or similar, as opposed to prior-year columns like "PYTD" or "PY Total") as "netSales". Extracting with a reasonable best-guess column is always better than extracting nothing.
        - ZERO-VALUE ROWS ARE NOT OPTIONAL — YOU MUST EMIT THEM: if a row names a customer/entity but its value for the target period is zero, blank, or an extremely small floating-point artifact (e.g. 0.0000000001, effectively rounding error from spreadsheet formulas), set "netSales" to 0 and EMIT THE ROW ANYWAY. A customer with no activity this period is still a row in the output — do not treat "zero" as a reason to drop it, and never invent a nonzero figure for it either. This document set is compared against a fixed reference schema row-for-row, so silently dropping zero-activity entities is a data-loss bug, exactly like dropping any other row.
        - Date precision: use the exact day if it is explicitly present in the source (including Excel serial date numbers). If only a month and year are known for a given row (including pivot-style reports where the whole file covers one period), use the FIRST day of that month. Always output a complete yyyy-MM-dd date — never omit the day.
        - Excel serial date numbers (e.g. 45453) must be converted using December 30, 1899 as day zero (NOT December 31, 1899).
        - Normalize numeric values (strip currency symbols, thousands separators).
        - "sourceName" should reflect the source file, sheet, or manufacturer name if available.
        - If a field is not present in the source document, use null.
        - Even messy or merged table structures may still contain valid entity/value pairs — extract them by identifying recognizable entity names paired with numeric values, ignoring formatting noise.
        - NEVER emit a row that has neither a customer/entity name NOR any monetary column at all in the source (i.e. the row is structurally empty — a title, page header, period banner, or blank separator line). This is about the row having no data to extract, NOT about the number being zero: a row that names a customer/entity always qualifies for extraction, even when every money column on it reads 0 or blank — see the zero-value rule above.
        - Dates in the content you receive are already normalized to yyyy-MM-dd. Copy them exactly as given. Do not re-derive, shift or recompute them.
        - SKIP TOTAL AND SUBTOTAL ROWS: a row is an aggregate of rows you have already extracted — and must never be emitted, however large its numbers — if ANY cell in that row (not just the first one) reads like an aggregate label rather than a data value: "Total", "Totals for Rep No 90", "Totals for Territory", "Grand Total", "Subtotal", "Sum", "Result", "Overall Result". Multi-level pivot/SAP-style exports nest several tiers of subtotal (by product group, then by customer, then by division, then a grand total), and the label marking each tier can land in a different column depending on how many grouping levels are collapsed on that row — checking only the first cell misses most of them.

        COLUMN CONSISTENCY — reports often contain SEVERAL similar money columns side by side (e.g. a gross order amount, a per-representative allocated amount, and a second representative's share; or several commission columns). Whichever money column you use for "netSales", you MUST use that SAME column for EVERY row of the document. Never switch columns from one row to the next, and never fall back to a neighbouring column because a row's value is blank or zero — a blank in the chosen column means zero, not "read another column".

        ONE ROW IN, ONE ROW OUT — this is the most important rule:
        - You will often receive only a slice of a larger document, clearly marked "chunk X of Y". The slice tells you exactly how many data rows it contains.
        - Emit one output object per input data row, in the same order, unless the row is a pure aggregate as defined above.
        - Never stop early, never summarize, never merge several input rows into one, and never say there are "too many" rows. Returning fewer rows than you were given, other than skipped aggregates, is a data loss bug.
        - If you are running low on space, prefer emitting MORE rows with fewer optional fields filled in over emitting FEWER rows fully filled in. netSales, commission and customerName are the fields that matter most.

        IMPORTANT — about the fallback context you may receive below (manufacturer, period month/year):
        - These values are DEFAULTS supplied by the user for cases where the document itself does not state them explicitly. They are NOT search criteria.
        - Never refuse to extract data, and never filter out rows, because they don't match or "confirm" this fallback context.
        - Only use a fallback value to fill a field when that specific piece of information is genuinely absent from the source document. If the document itself states a different manufacturer or date, ALWAYS prefer what is actually written in the document.
        - For pivot-style reports (see rule above), the fallback period tells you WHICH money column to read as "netSales".
        """;

    /// <summary>
    /// Builds the system message. User instructions go HERE, at the end, with an explicit
    /// precedence rule — not in the user message where they sat behind ~25 imperative default
    /// rules and lost every conflict (e.g. "ignore the YTD block" vs. the pivot rule above).
    /// </summary>
    private static string BuildSystemPrompt(string? customInstructions, string? columnPlan = null, bool hasCollapsedPivotRows = false)
    {
        var prompt = BaseSystemPrompt;

        // The column map goes before the user overrides so that operator instructions still win.
        if (!string.IsNullOrWhiteSpace(columnPlan))
            prompt = $"{prompt}\n\n{columnPlan}";

        // A hierarchical Excel PivotTable (Eemax's "PT-578 C1"/"C2" sheets, for example) was already
        // collapsed by MarkdownTableNormalizer down to its single Grand Total row, relabelled
        // "ALL CUSTOMERS COMBINED" precisely so it doesn't visually read as a total. Without this,
        // the model still recognised it AS an aggregate by meaning rather than by keyword and
        // silently returned zero rows for the whole table — observed on real Eemax files even after
        // the relabel. Spelling out the exception explicitly is what stops that.
        if (hasCollapsedPivotRows)
        {
            prompt = $"""
                {prompt}

                ============ PRE-COLLAPSED PIVOT ROW — READ CAREFULLY ============
                At least one table below originally had many nested subtotal rows: a hierarchical
                Excel PivotTable outline where the same dollar amount repeats at every level of a
                customer > sub-account > order > invoice-line hierarchy. There is no reliable way to
                attribute that amount to one specific leaf entity, so a deterministic pass already
                collapsed the WHOLE table down to a single row holding the workbook's own correct
                Grand Total for the period, and labelled it "ALL CUSTOMERS COMBINED".
                This row is real data, not a total/subtotal to skip, even though it summarizes many
                customers and even though the rule above says to skip aggregate rows — that rule does
                not apply here. If you see a table whose only row is named "ALL CUSTOMERS COMBINED",
                you MUST extract it exactly like any other row: set customerName to
                "ALL CUSTOMERS COMBINED" and map its money columns normally. Returning zero rows for
                such a table is a data-loss bug, not a correct application of the skip-aggregates rule.
                ====================================================================
                """;
        }

        if (string.IsNullOrWhiteSpace(customInstructions)) return prompt;

        return $"""
            {prompt}

            ================= USER OVERRIDES =================
            The operator supplied the following instructions for THIS document. They take precedence
            over any default rule above wherever the two conflict — if a default rule says to extract
            something and the operator says to skip it (or vice versa), follow the operator.
            The only things these instructions cannot override are the fixed JSON schema and the
            prohibition on inventing values that are not in the source document.

            {customInstructions.Trim()}
            ==================================================
            """;
    }

    // ---------------------------------------------------------------------
    // Markdown / table parsing
    // ---------------------------------------------------------------------

    private static string ExtractMarkdown(string rawExtractionJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawExtractionJson);
            var sb = new StringBuilder();

            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("contents", out var contents))
            {
                foreach (var content in contents.EnumerateArray())
                {
                    if (content.TryGetProperty("markdown", out var md))
                    {
                        sb.AppendLine(md.GetString());
                        sb.AppendLine();
                    }
                }
            }

            var extracted = sb.ToString();
            return string.IsNullOrWhiteSpace(extracted) ? rawExtractionJson : extracted;
        }
        catch
        {
            return rawExtractionJson; // fallback to raw JSON if parsing fails, same as before
        }
    }

    internal static readonly Regex TableRowRegex = new(@"^\s*\|.*\|\s*$", RegexOptions.Compiled);
    internal static readonly Regex TableSeparatorRegex = new(@"^\s*\|[\s:\-\|]+\|\s*$", RegexOptions.Compiled);

    internal record TableParts(string Preamble, string HeaderBlock, List<string> DataRows);

    private static readonly Regex SheetHeadingInPreambleRegex = new(@"^#\s+(?<name>.+?)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Reads the pinned money column's value straight from each source data row, in the same
    /// document order chunks are built in (tables in order, each table's DataRows in order) — the
    /// exact order allSales ends up in when the model preserves row order, which is separately
    /// enforced and verified. Returns null (meaning: don't trust this, skip the override) if any
    /// table with data rows doesn't have a resolvable pinned column, rather than risk a
    /// partial/misaligned correction.
    ///
    /// A workbook whose sheets each need a different money column (e.g. one column per product
    /// line, differently named per sheet) resolves each table's prefix from perSheetPrefixes,
    /// keyed by the "# SheetName" heading SpreadsheetExtractionService writes ahead of that
    /// sheet's table — falling back to the single global prefix for sheets not named there.
    /// </summary>
    private static List<double?>? ExtractPinnedColumnValues(
        List<TableParts> tables, string? globalPrefix, IReadOnlyDictionary<string, string>? perSheetPrefixes)
    {
        var values = new List<double?>();

        foreach (var table in tables)
        {
            if (table.DataRows.Count == 0) continue;

            var sheetName = SheetHeadingInPreambleRegex.Match(table.Preamble) is { Success: true } hm
                ? hm.Groups["name"].Value : null;
            var prefix = sheetName is not null && perSheetPrefixes is not null
                && perSheetPrefixes.TryGetValue(sheetName, out var pinned)
                ? pinned
                : globalPrefix;
            if (prefix is null) return null;

            var headerLine = table.HeaderBlock.Split('\n')[0];
            var headerCells = SplitMarkdownRow(headerLine);
            var pinnedIdx = headerCells.FindIndex(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (pinnedIdx < 0) return null;

            foreach (var row in table.DataRows)
            {
                var cells = SplitMarkdownRow(row);
                var raw = pinnedIdx < cells.Count ? cells[pinnedIdx] : "";
                values.Add(double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null);
            }
        }

        return values;
    }

    // Matches a cell reading like "Total Territory Sales:", "Grand Total:", "Total Sales:" — a
    // report's own stated aggregate, printed as a plain label rather than sitting in a regular data
    // column. Built for a real Kutol Sales territory commission report whose per-customer and
    // grand-total rows carry NO text label at all in their normal columns (blank Item Code, blank
    // Item — nothing for IsTotalRow or column position to key off), but which separately prints
    // "Total Territory Sales: 146350.98" as its own line near the end. Column alignment is
    // unreliable on that report (rows vary in cell count row to row), so this deliberately searches
    // raw markdown text rather than a fixed column index.
    // Second alternative catches a MotorScrubber/Western Sales export style: a "Grand Summary:" row
    // sitting right under the header, in a table where every real line's "Total excl. shipping" is
    // actually the PARENT INVOICE's total repeated on every item line of that invoice (not a
    // per-item amount) — so summing the model's extracted rows naturally double-counts any invoice
    // with more than one line, and there is no per-row fix for that short of re-deriving invoice
    // grouping. The "Grand Summary:" row states the real total in plain text instead.
    private static readonly Regex LabeledGrandTotalRegex = new(
        @"\btotal\s+\w+\s+sales\s*:|\bgrand\s+summary\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Searched against the ORIGINAL, un-normalized markdown — not a table's DataRows — because this
    // label's own row also matches TotalRowRegex (it starts with "Total"/"Grand Summary") and is
    // dropped as an aggregate row well before DataRows is populated, same as any other total row.
    // That drop is correct for the row's role as a subtotal in the detail table; it just also
    // happens to be the one place this document states its real answer in plain text.
    private static double? FindLabeledGrandTotal(string rawMarkdown)
    {
        foreach (var row in rawMarkdown.Split('\n'))
        {
            var match = LabeledGrandTotalRegex.Match(row);
            if (!match.Success) continue;

            // The LAST number on the line, not the first: a markdown table row can carry other
            // numeric columns (e.g. a "Total Quantity" count) between the label and the actual money
            // total, which sits in the rightmost/money column — as seen on the MotorScrubber
            // "Grand Summary:" row ("| Grand Summary: | | | | | 4 | 2396.14 |", where the first
            // number after the label is the quantity 4, not the $2,396.14 total).
            var numberMatches = Regex.Matches(row[(match.Index + match.Length)..], @"-?[\d,]*\d\.?\d*");
            for (int m = numberMatches.Count - 1; m >= 0; m--)
            {
                if (double.TryParse(numberMatches[m].Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static List<string> SplitMarkdownRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed[1..];
        if (trimmed.EndsWith("|")) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    /// <summary>
    /// Finds EVERY markdown table in the document, not just the first one.
    ///
    /// The previous version located the first header/separator pair and then treated every
    /// remaining pipe-delimited line in the whole file as a data row of that table. In a
    /// multi-sheet workbook that silently glued sheet 2's rows — with different columns — under
    /// sheet 1's header, so the model read amounts out of the wrong column. That is a direct
    /// cause of totals that don't match the source.
    ///
    /// Each table now carries the free text that preceded it (sheet title, period, commission
    /// rates) as its own preamble.
    /// </summary>
    internal static List<TableParts> SplitTables(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var tables = new List<TableParts>();

        var preamble = new StringBuilder();
        int i = 0;

        while (i < lines.Length)
        {
            var isHeader = i < lines.Length - 1
                && TableRowRegex.IsMatch(lines[i])
                && TableSeparatorRegex.IsMatch(lines[i + 1]);

            if (!isHeader)
            {
                preamble.AppendLine(lines[i]);
                i++;
                continue;
            }

            var headerBlock = lines[i] + "\n" + lines[i + 1];
            i += 2;

            // A table ends at the first line that is not a table row. A blank line inside a table
            // is tolerated only if a table row follows it (Content Understanding sometimes emits
            // one), otherwise it closes the table.
            var dataRows = new List<string>();
            while (i < lines.Length)
            {
                if (TableRowRegex.IsMatch(lines[i]))
                {
                    // A second separator means a new table started without intervening text.
                    if (TableSeparatorRegex.IsMatch(lines[i])) break;
                    dataRows.Add(lines[i]);
                    i++;
                }
                else if (string.IsNullOrWhiteSpace(lines[i])
                      && i + 1 < lines.Length && TableRowRegex.IsMatch(lines[i + 1])
                      && !TableSeparatorRegex.IsMatch(lines[i + 1]))
                {
                    i++;
                }
                else break;
            }

            if (dataRows.Count > 0)
                tables.Add(new TableParts(Trim(preamble.ToString()), headerBlock, dataRows));

            preamble.Clear();
        }

        // No table at all (scanned PDF, free text): expose the whole document as a single
        // preamble-only part so the caller falls back to paragraph chunking.
        if (tables.Count == 0)
            tables.Add(new TableParts(markdown, "", new List<string>()));

        return tables;
    }

    private static string Trim(string preamble)
    {
        var text = preamble.Trim();
        return text.Length <= MaxPreambleChars ? text : text[^MaxPreambleChars..];
    }

    internal record Chunk(string Content, int RowCount, string TableLabel);

    /// <summary>
    /// Splits every table into row-based chunks. The preamble is repeated in EVERY chunk (it used
    /// to be attached to chunk 0 only, so every later chunk lost the report period and any
    /// commission rate stated in the sheet header — exactly what the pivot and "rates in header"
    /// rules need in order to pick the right column).
    /// </summary>
    internal static List<Chunk> BuildChunks(List<TableParts> tables, int targetRowsPerChunk) =>
        BuildChunksWithTableBoundaries(tables, targetRowsPerChunk).Chunks;

    /// <summary>
    /// Same output as <see cref="BuildChunks"/>, plus how many consecutive chunks each table
    /// contributed — chunks for one table are always contiguous (the loop below finishes a table
    /// before starting the next), so this is enough to slice a flat chunk-results array back into
    /// per-table groups without needing row counts, which can't be trusted to still line up once
    /// the model has dropped or added rows.
    /// </summary>
    internal static (List<Chunk> Chunks, List<int> ChunksPerTable) BuildChunksWithTableBoundaries(
        List<TableParts> tables, int targetRowsPerChunk)
    {
        var chunks = new List<Chunk>();
        var chunksPerTable = new List<int>();

        for (int t = 0; t < tables.Count; t++)
        {
            var countBefore = chunks.Count;
            var parts = tables[t];
            var label = tables.Count > 1 ? $"table {t + 1} of {tables.Count}" : "document";

            if (parts.DataRows.Count == 0)
            {
                // Free-text fallback: paragraph-based chunking.
                const int targetChars = 45_000;
                var paragraphs = parts.Preamble.Split("\n\n");
                var current = new StringBuilder();
                foreach (var p in paragraphs)
                {
                    if (current.Length + p.Length > targetChars && current.Length > 0)
                    {
                        chunks.Add(new Chunk(current.ToString(), 0, label));
                        current.Clear();
                    }
                    current.AppendLine(p).AppendLine();
                }
                if (current.Length > 0) chunks.Add(new Chunk(current.ToString(), 0, label));
                chunksPerTable.Add(chunks.Count - countBefore);
                continue;
            }

            for (int i = 0; i < parts.DataRows.Count; i += targetRowsPerChunk)
            {
                var slice = parts.DataRows.Skip(i).Take(targetRowsPerChunk).ToList();
                var sb = new StringBuilder();

                if (!string.IsNullOrWhiteSpace(parts.Preamble))
                    sb.AppendLine(parts.Preamble).AppendLine();

                sb.AppendLine(parts.HeaderBlock);
                foreach (var row in slice) sb.AppendLine(row);

                chunks.Add(new Chunk(sb.ToString(), slice.Count, label));
            }

            chunksPerTable.Add(chunks.Count - countBefore);
        }

        return (chunks, chunksPerTable);
    }

    /// <summary>Cuts an already-built chunk in half, keeping the header and preamble in both halves.</summary>
    private static (Chunk, Chunk)? SplitChunkInHalf(Chunk chunk)
    {
        var lines = chunk.Content.Replace("\r\n", "\n").Split('\n');

        int headerIdx = -1;
        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (TableRowRegex.IsMatch(lines[i]) && TableSeparatorRegex.IsMatch(lines[i + 1]))
            {
                headerIdx = i;
                break;
            }
        }

        if (headerIdx < 0) return null; // free-text chunk — nothing structured to split on

        var preamble = string.Join('\n', lines.Take(headerIdx)).Trim();
        var header = lines[headerIdx] + "\n" + lines[headerIdx + 1];
        var dataRows = lines.Skip(headerIdx + 2).Where(l => TableRowRegex.IsMatch(l)).ToList();

        if (dataRows.Count < MinRowsToSplit) return null;

        var mid = dataRows.Count / 2;

        Chunk Build(IEnumerable<string> rows)
        {
            var list = rows.ToList();
            var sb = new StringBuilder();
            if (preamble.Length > 0) sb.AppendLine(preamble).AppendLine();
            sb.AppendLine(header);
            foreach (var r in list) sb.AppendLine(r);
            return new Chunk(sb.ToString(), list.Count, chunk.TableLabel);
        }

        return (Build(dataRows.Take(mid)), Build(dataRows.Skip(mid)));
    }

    // ---------------------------------------------------------------------
    // Column plan — decided ONCE per document
    // ---------------------------------------------------------------------

    private static readonly BinaryData ColumnPlanSchema = BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "netSalesColumn":       { "type": "string", "description": "Exact header text of the single column to use as netSales for EVERY row" },
        "netSalesReasoning":    { "type": "string", "description": "Why this column and not the other money columns" },
        "commissionColumn":     { "type": ["string", "null"] },
        "customerIdColumn":     { "type": ["string", "null"] },
        "customerNameColumn":   { "type": ["string", "null"] },
        "dateColumn":           { "type": ["string", "null"] },
        "quantityColumn":       { "type": ["string", "null"] },
        "partNoColumn":         { "type": ["string", "null"] },
        "partDescriptionColumn":{ "type": ["string", "null"] },
        "cityColumn":           { "type": ["string", "null"] },
        "stateColumn":          { "type": ["string", "null"] },
        "productFamilyColumn":  { "type": ["string", "null"] },
        "ignoredMoneyColumns":  { "type": "array", "items": { "type": "string" }, "description": "Other money columns that must NOT be used as netSales" },
        "skipRowRule":          { "type": "string", "description": "How to recognise total/subtotal rows in this specific document" }
      },
      "required": ["netSalesColumn","netSalesReasoning","commissionColumn","customerIdColumn","customerNameColumn","dateColumn","quantityColumn","partNoColumn","partDescriptionColumn","cityColumn","stateColumn","productFamilyColumn","ignoredMoneyColumns","skipRowRule"],
      "additionalProperties": false
    }
    """);

    private sealed class ColumnPlan
    {
        public string NetSalesColumn { get; set; } = "";
        public string NetSalesReasoning { get; set; } = "";
        public string? CommissionColumn { get; set; }
        public string? CustomerIdColumn { get; set; }
        public string? CustomerNameColumn { get; set; }
        public string? DateColumn { get; set; }
        public string? QuantityColumn { get; set; }
        public string? PartNoColumn { get; set; }
        public string? PartDescriptionColumn { get; set; }
        public string? CityColumn { get; set; }
        public string? StateColumn { get; set; }
        public string? ProductFamilyColumn { get; set; }
        public List<string> IgnoredMoneyColumns { get; set; } = new();
        public string SkipRowRule { get; set; } = "";
    }

    private static readonly string ColumnPlanSystemPrompt = """
        You are given the header and a sample of rows from a manufacturer sales or commission report.
        Your job is NOT to extract data. It is to decide, ONCE for the whole document, which source
        column feeds each field of the canonical schema.

        The critical decision is "netSalesColumn". These reports commonly carry several money columns
        side by side, for example:
        - a gross order/invoice amount for the whole transaction (e.g. "TOT ORD", "Invoice Amount"),
        - one or more per-representative allocated amounts (e.g. "REP SLS1", "REP SLS2"), often split
          by a percentage column, where the same invoice is shared between two representatives,
        - prior-period, year-to-date, rolling or variance columns in pivot-style reports.

        Rules for choosing netSalesColumn:
        - Prefer the column that represents the NET SALES value attributed to the party this report is
          about, at the granularity of one row. In a per-representative commission report, that is the
          representative's own allocated sales column (the first one, e.g. "REP SLS1"), NOT the gross
          order total and NOT a second representative's share.
        - Never choose a prior-year, cumulative, rolling-total, difference or percentage column.
        - Choose exactly ONE column. List every other money column in "ignoredMoneyColumns".

        For "skipRowRule", describe how total/subtotal rows can be recognised in THIS document, quoting
        the actual label text you can see (e.g. rows whose first cell starts with "Totals for Rep No").

        Use the exact header text as it appears, so a later step can match it.
        """;

    /// <summary>
    /// Runs one cheap model call on the header plus a sample of rows to fix the column mapping for
    /// the whole document.
    ///
    /// This exists because of a concrete failure: on a commission report with three similar money
    /// columns (gross order total, rep 1 share, rep 2 share), the model was choosing a different
    /// column on different rows, producing a document total that matched no column in the source
    /// file. Deciding once, then repeating that decision as a binding constraint in every chunk,
    /// is what removes that drift.
    /// </summary>
    private async Task<string?> BuildColumnPlanAsync(
        List<TableParts> tables, string? customInstructions,
        string? fallbackManufacturer, string? fallbackPeriodMonth, string? fallbackPeriodYear,
        CancellationToken ct)
    {
        var tabular = tables.Where(t => t.DataRows.Count > 0).ToList();
        if (tabular.Count == 0) return null; // free-text document: nothing to map

        var sample = new StringBuilder();
        foreach (var (table, i) in tabular.Select((t, i) => (t, i)))
        {
            if (tabular.Count > 1) sample.AppendLine($"--- table {i + 1} of {tabular.Count} ---");
            if (!string.IsNullOrWhiteSpace(table.Preamble)) sample.AppendLine(table.Preamble);
            sample.AppendLine(table.HeaderBlock);

            foreach (var row in table.DataRows.Take(12)) sample.AppendLine(row);
            if (table.DataRows.Count > 15)
            {
                sample.AppendLine("... (rows omitted) ...");
                // The last rows are where total/subtotal lines live — the model needs to see them
                // in order to describe how to recognise and skip them.
                foreach (var row in table.DataRows.TakeLast(3)) sample.AppendLine(row);
            }
            sample.AppendLine();
        }

        var userParts = new List<string>
        {
            $"Header and sample rows:\n{sample}",
            $"Reporting context: manufacturer {fallbackManufacturer ?? "unknown"}, period {fallbackPeriodMonth ?? "??"}/{fallbackPeriodYear ?? "????"}."
        };

        if (!string.IsNullOrWhiteSpace(customInstructions))
        {
            userParts.Add($"""
                Operator instructions — these override the guidance above when they conflict, including
                the choice of money column if they name one explicitly:
                {customInstructions.Trim()}
                """);
        }

        try
        {
            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "column_plan",
                    jsonSchema: ColumnPlanSchema,
                    jsonSchemaIsStrict: true),
                Temperature = 0f
            };

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(ColumnPlanSystemPrompt),
                new UserChatMessage(string.Join("\n\n", userParts))
            };

            var completion = await CompleteWithTransientRetryAsync(messages, options, ct);
            if (completion.Content.Count == 0) return null;

            var plan = JsonSerializer.Deserialize<ColumnPlan>(
                completion.Content[0].Text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (plan is null || string.IsNullOrWhiteSpace(plan.NetSalesColumn)) return null;

            _logger.LogInformation(
                "Column plan: netSales <- '{NetSales}' ({Reason}); ignoring [{Ignored}]",
                plan.NetSalesColumn, plan.NetSalesReasoning, string.Join(", ", plan.IgnoredMoneyColumns));

            return FormatPlan(plan);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A missing plan is not fatal — extraction still runs on the general rules.
            _logger.LogWarning(ex, "Could not build a column plan; falling back to per-chunk inference");
            return null;
        }
    }

    private static string FormatPlan(ColumnPlan plan)
    {
        var lines = new List<string>
        {
            $"- netSales MUST be read from the column \"{plan.NetSalesColumn}\" for EVERY row, without exception."
        };

        if (plan.IgnoredMoneyColumns.Count > 0)
            lines.Add($"- These money columns must NEVER be used as netSales: {string.Join(", ", plan.IgnoredMoneyColumns.Select(c => $"\"{c}\""))}.");

        void Add(string field, string? column)
        {
            if (!string.IsNullOrWhiteSpace(column)) lines.Add($"- {field} <- column \"{column}\"");
        }

        Add("commission", plan.CommissionColumn);
        Add("customerId", plan.CustomerIdColumn);
        Add("customerName", plan.CustomerNameColumn);
        Add("date", plan.DateColumn);
        Add("quantity", plan.QuantityColumn);
        Add("partNo", plan.PartNoColumn);
        Add("partDescription", plan.PartDescriptionColumn);
        Add("city", plan.CityColumn);
        Add("state", plan.StateColumn);
        Add("productFamily", plan.ProductFamilyColumn);

        if (!string.IsNullOrWhiteSpace(plan.SkipRowRule))
            lines.Add($"- Skip these rows entirely: {plan.SkipRowRule}");

        return $"""
            ============ BINDING COLUMN MAP FOR THIS DOCUMENT ============
            A first pass already analysed this document's header and decided the mapping below.
            Apply it exactly. Do not re-derive it, do not second-guess it, and do not switch columns
            partway through — every chunk of this document is being mapped with these same rules, so
            any deviation produces a document total that matches nothing in the source file.
            Fields with no column listed below are not present in this document: emit them as null.

            {string.Join("\n", lines)}
            =============================================================
            """;
    }

    // ---------------------------------------------------------------------
    // Public entry point
    // ---------------------------------------------------------------------

    public async Task<TransformationResult> TransformAsync(
        string rawExtractionJson,
        string? fallbackManufacturer,
        string? fallbackPeriodMonth,
        string? fallbackPeriodYear,
        string? customInstructions,
        Func<double, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        var rawMarkdown = ExtractMarkdown(rawExtractionJson);

        // Deterministic clean-up first: real header detection, merged-column de-duplication,
        // Excel serial dates converted exactly, aggregate rows removed, float noise stripped, and —
        // when the operator pinned one via custom instructions — every competing money column
        // physically removed so the model has nothing left to drift into.
        var moneyColumnPrefix = CustomInstructionsParser.TryExtractMoneyColumnPrefix(customInstructions);
        var perSheetMoneyColumnPrefixes = CustomInstructionsParser.TryExtractPerSheetMoneyColumnPrefixes(customInstructions);
        var perSheetRowFilters = CustomInstructionsParser.TryExtractRowFilters(customInstructions);
        var normalized = MarkdownTableNormalizer.Normalize(rawMarkdown, moneyColumnPrefix, perSheetMoneyColumnPrefixes, perSheetRowFilters);
        _logger.LogInformation(
            "Markdown normalized: {Columns} duplicate/empty column(s) removed, {Totals} aggregate row(s) dropped",
            normalized.DroppedColumns, normalized.DroppedTotalRows);

        var tables = SplitTables(normalized.Markdown);
        var (chunks, chunksPerTable) = BuildChunksWithTableBoundaries(tables, TargetRowsPerChunk);

        // One cheap call decides the column mapping for the whole document, before any row is
        // extracted. Every chunk then receives that same decision as a binding constraint.
        var columnPlan = await BuildColumnPlanAsync(
            tables, customInstructions, fallbackManufacturer, fallbackPeriodMonth, fallbackPeriodYear, ct);

        var systemPrompt = BuildSystemPrompt(customInstructions, columnPlan, normalized.HasCollapsedPivotRows);

        var results = new List<AnalyticsTransaction>?[chunks.Count];
        var warnings = new ConcurrentBag<string>();
        var completed = 0;
        var progressLock = new SemaphoreSlim(1, 1);

        var tasks = chunks.Select(async (chunk, i) =>
        {
            // Shared across every document being processed right now, not just this one — see
            // OpenAiConcurrencyLimiter for why a per-document limit alone let concurrent uploads
            // multiply the actual load on Azure OpenAI far past its real quota.
            await _limiter.WaitAsync(ct);
            try
            {
                results[i] = await TransformChunkWithRetryAsync(
                    chunk, i + 1, chunks.Count, systemPrompt,
                    fallbackManufacturer, fallbackPeriodMonth, fallbackPeriodYear, warnings, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad chunk must not discard the other 30. It is recorded as a warning so the
                // document is flagged instead of quietly reporting a total that is missing rows.
                _logger.LogError(ex, "Chunk {Index}/{Count} failed permanently", i + 1, chunks.Count);
                warnings.Add($"Chunk {i + 1}/{chunks.Count} ({chunk.TableLabel}) failed after retries: {ex.Message}. Its ~{chunk.RowCount} rows are missing from the totals.");
                results[i] = new List<AnalyticsTransaction>();
            }
            finally
            {
                _limiter.Release();
            }

            var done = Interlocked.Increment(ref completed);
            if (onProgress is not null)
            {
                // sérialise les appels à onProgress car il écrit en base (EF DbContext non thread-safe)
                await progressLock.WaitAsync(ct);
                try { await onProgress(done / (double)chunks.Count); }
                finally { progressLock.Release(); }
            }
        });

        await Task.WhenAll(tasks);

        var allSales = results.Where(r => r is not null).SelectMany(r => r!).ToList();

        foreach (var sale in allSales)
        {
            if (sale.NetSales.HasValue) sale.NetSales = Math.Round(sale.NetSales.Value, 2);
            if (sale.Commission.HasValue) sale.Commission = Math.Round(sale.Commission.Value, 2);
        }

        // When the operator pinned an exact netSales column, don't trust the model to have
        // transcribed every figure correctly — read it back from the source deterministically and
        // overwrite. The model is still the one deciding row order/skipping/customer fields; this
        // only replaces the one number it has been observed to occasionally mistranscribe on very
        // long, many-chunk documents.
        //
        // Applied per TABLE, not once across the whole document: chunk boundaries never cross a
        // table (chunksPerTable records exactly how many consecutive chunks belong to each one), so
        // a single table's chunk(s) dropping a row only breaks the safety check — and therefore only
        // forfeits the override — for THAT table. A whole-document check was observed to throw away
        // the override for every sheet in a 6-sheet workbook because one sheet lost a handful of
        // rows, even though the other five were extracted perfectly.
        var moneyColumnPinApplied = false;
        {
            var chunkCursor = 0;
            for (int t = 0; t < tables.Count; t++)
            {
                var chunkCount = chunksPerTable[t];
                var tableSales = Enumerable.Range(chunkCursor, chunkCount)
                    .SelectMany(i => results[i] ?? new List<AnalyticsTransaction>())
                    .ToList();
                chunkCursor += chunkCount;

                List<double?>? tablePinnedValues = null;
                if (!string.IsNullOrWhiteSpace(moneyColumnPrefix) || perSheetMoneyColumnPrefixes is not null)
                {
                    tablePinnedValues = ExtractPinnedColumnValues(
                        new List<TableParts> { tables[t] }, moneyColumnPrefix, perSheetMoneyColumnPrefixes);
                }

                if (tablePinnedValues is not null)
                {
                    moneyColumnPinApplied = true;

                    if (tablePinnedValues.Count == tableSales.Count)
                    {
                        for (int i = 0; i < tableSales.Count; i++)
                            tableSales[i].NetSales = tablePinnedValues[i];
                        continue;
                    }
                }

                if (tableSales.Count == 0) continue;

                // No pinned column, or its row count didn't line up with the model's (the model
                // added or dropped a row somewhere across this table's chunks, so per-row alignment
                // isn't safe) — but some reports print their OWN grand total in plain text, e.g. a
                // Kutol Sales territory commission report ending in a line reading literally "Total
                // Territory Sales: 146350.98". That needs no column alignment at all: find it, and
                // it is exactly right regardless of anything the model did. Falls back to the pinned
                // column's own sum (still alignment-free) when no such label exists.
                double? detTotal = FindLabeledGrandTotal(rawMarkdown)
                    ?? (tablePinnedValues is not null
                        ? tablePinnedValues.Where(v => v.HasValue).Sum(v => v!.Value)
                        : null);
                if (detTotal is null) continue;

                var llmTotal = tableSales.Sum(s => s.NetSales ?? 0);

                if (Math.Abs(llmTotal) > 0.01)
                {
                    var scale = detTotal.Value / llmTotal;
                    foreach (var sale in tableSales)
                        if (sale.NetSales.HasValue) sale.NetSales = Math.Round(sale.NetSales.Value * scale, 2);
                }
                else
                {
                    // Nothing to scale (the model reported ~$0 across the board) — spread the real
                    // total evenly rather than silently leave it at zero.
                    var each = Math.Round(detTotal.Value / tableSales.Count, 2);
                    foreach (var sale in tableSales) sale.NetSales = each;
                }

                moneyColumnPinApplied = true;
                warnings.Add(
                    $"{tables[t].Preamble.Trim().Split('\n').FirstOrDefault() ?? "A table"}: row count from the " +
                    $"model ({tableSales.Count}) didn't match a reliable deterministic source, so per-row figures " +
                    "were rescaled to match that source's true total rather than left as reported.");
            }
        }

        var rowsSent = chunks.Sum(c => c.RowCount);
        var rowsReturned = allSales.Count;

        return new TransformationResult(
            new AnalyticsReport { Sales = allSales },
            warnings.OrderBy(w => w).ToList(),
            rowsSent,
            rowsReturned,
            moneyColumnPinApplied);
    }

    /// <summary>
    /// Calls the model for one chunk. If the response was cut off (FinishReason.Length) or fails
    /// to deserialize, the chunk is split in half and each half is retried recursively.
    /// Every case where data can be lost now records a warning rather than returning quietly.
    /// </summary>
    private async Task<List<AnalyticsTransaction>> TransformChunkWithRetryAsync(
        Chunk chunk, int chunkIndex, int chunkCount, string systemPrompt,
        string? fallbackManufacturer, string? fallbackPeriodMonth, string? fallbackPeriodYear,
        ConcurrentBag<string> warnings, CancellationToken ct)
    {
        List<AnalyticsTransaction> sales;
        bool truncated;

        try
        {
            (sales, truncated) = await CallModelAsync(
                chunk, chunkIndex, chunkCount, systemPrompt,
                fallbackManufacturer, fallbackPeriodMonth, fallbackPeriodYear, ct);
        }
        catch (JsonException ex)
        {
            var halves = SplitChunkInHalf(chunk);
            if (halves is null)
            {
                warnings.Add($"Chunk {chunkIndex}/{chunkCount} ({chunk.TableLabel}) returned unparseable JSON and is too small to split further ({chunk.RowCount} rows lost): {ex.Message}");
                return new List<AnalyticsTransaction>();
            }

            return await SplitAndRecurseAsync(halves.Value, chunkIndex, chunkCount, systemPrompt,
                fallbackManufacturer, fallbackPeriodMonth, fallbackPeriodYear, warnings, ct);
        }

        if (truncated)
        {
            var halves = SplitChunkInHalf(chunk);
            if (halves is null)
            {
                warnings.Add($"Chunk {chunkIndex}/{chunkCount} ({chunk.TableLabel}) hit the output limit and cannot be split further — kept {sales.Count} of ~{chunk.RowCount} rows.");
                return sales;
            }

            return await SplitAndRecurseAsync(halves.Value, chunkIndex, chunkCount, systemPrompt,
                fallbackManufacturer, fallbackPeriodMonth, fallbackPeriodYear, warnings, ct);
        }

        // Per-chunk reconciliation. A shortfall here is invisible in the document-level total,
        // which is precisely how missing rows used to go unnoticed.
        if (chunk.RowCount > 0 && sales.Count < chunk.RowCount * 0.7)
        {
            warnings.Add($"Chunk {chunkIndex}/{chunkCount} ({chunk.TableLabel}) returned {sales.Count} rows for {chunk.RowCount} input rows — possible dropped rows.");
        }

        return sales;
    }

    private async Task<List<AnalyticsTransaction>> SplitAndRecurseAsync(
        (Chunk, Chunk) halves, int chunkIndex, int chunkCount, string systemPrompt,
        string? fallbackManufacturer, string? fallbackPeriodMonth, string? fallbackPeriodYear,
        ConcurrentBag<string> warnings, CancellationToken ct)
    {
        var (a, b) = halves;
        var first = await TransformChunkWithRetryAsync(a, chunkIndex, chunkCount, systemPrompt,
            fallbackManufacturer, fallbackPeriodMonth, fallbackPeriodYear, warnings, ct);
        var second = await TransformChunkWithRetryAsync(b, chunkIndex, chunkCount, systemPrompt,
            fallbackManufacturer, fallbackPeriodMonth, fallbackPeriodYear, warnings, ct);
        first.AddRange(second);
        return first;
    }

    private async Task<(List<AnalyticsTransaction> Sales, bool Truncated)> CallModelAsync(
        Chunk chunk, int chunkIndex, int chunkCount, string systemPrompt,
        string? fallbackManufacturer, string? fallbackPeriodMonth, string? fallbackPeriodYear,
        CancellationToken ct)
    {
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "analytics_report",
                jsonSchema: Schema,
                jsonSchemaIsStrict: true),

            // Deterministic transcription, not creative writing. Also makes truncation reliably
            // observable through FinishReason instead of being masked by a variable-length answer.
            Temperature = 0f
        };

        var userMessageParts = new List<string>
        {
            chunkCount > 1
                ? $"Extracted document content — chunk {chunkIndex} of {chunkCount} ({chunk.TableLabel}). This is a slice of a larger document; the other chunks are handled in separate calls. This slice contains {chunk.RowCount} data rows and you are expected to return one object per data row (minus pure aggregates):\n{chunk.Content}"
                : $"Extracted document content ({chunk.RowCount} data rows — return one object per data row, minus pure aggregates):\n{chunk.Content}"
        };

        if (!string.IsNullOrWhiteSpace(fallbackManufacturer) || !string.IsNullOrWhiteSpace(fallbackPeriodMonth) || !string.IsNullOrWhiteSpace(fallbackPeriodYear))
        {
            userMessageParts.Add($"""
                Fallback context (defaults only, use ONLY if the document itself doesn't state it):
                - Manufacturer: {fallbackManufacturer ?? "unknown"}
                - Period: {fallbackPeriodMonth ?? "??"}/{fallbackPeriodYear ?? "????"}
                """);
        }

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(string.Join("\n\n", userMessageParts))
        };

        var completion = await CompleteWithTransientRetryAsync(messages, options, ct);

        var truncated = completion.FinishReason == ChatFinishReason.Length;
        var resultJson = completion.Content.Count > 0 ? completion.Content[0].Text : "";

        if (string.IsNullOrWhiteSpace(resultJson))
            return (new List<AnalyticsTransaction>(), truncated);

        var report = JsonSerializer.Deserialize<AnalyticsReport>(resultJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new AnalyticsReport();

        return (report.Sales, truncated);
    }

    /// <summary>
    /// Retries throttling (429) and transient server errors with exponential backoff plus jitter,
    /// honouring Retry-After when Azure sends it. Without this, a single 429 — which is routine
    /// when a batch of files is in flight — failed a whole chunk and silently removed its rows
    /// from the document total.
    /// </summary>
    private async Task<ChatCompletion> CompleteWithTransientRetryAsync(
        List<ChatMessage> messages, ChatCompletionOptions options, CancellationToken ct)
    {
        var jitter = new Random();

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var completion = await _chatClient.CompleteChatAsync(messages, options, ct);
                return completion.Value;
            }
            catch (ClientResultException ex) when (IsTransient(ex.Status) && attempt < MaxTransientRetries)
            {
                var delay = RetryAfter(ex)
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt) + jitter.NextDouble() * 2);

                _logger.LogWarning("Azure OpenAI returned {Status}; retrying in {Delay}s (attempt {Attempt}/{Max})",
                    ex.Status, delay.TotalSeconds, attempt, MaxTransientRetries);

                await Task.Delay(delay, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxTransientRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) + jitter.NextDouble() * 2);
                _logger.LogWarning("Azure OpenAI call timed out; retrying in {Delay}s (attempt {Attempt}/{Max})",
                    delay.TotalSeconds, attempt, MaxTransientRetries);
                await Task.Delay(delay, ct);
            }
            // The underlying transport (System.ClientModel's ClientRetryPolicy) retries a stalled
            // HTTP call internally, then gives up by throwing an AggregateException — NOT a plain
            // TaskCanceledException — once it has exhausted its own attempts. That exception type
            // never matched the catch above, so every chunk that hit this path failed permanently
            // on its very first attempt through OUR loop, silently dropping ~90 rows from the
            // totals each time. Unwrap and treat the same timeout as transient here too.
            catch (AggregateException ex) when (!ct.IsCancellationRequested
                && attempt < MaxTransientRetries
                && IsTimeoutAggregate(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt) + jitter.NextDouble() * 2);
                _logger.LogWarning("Azure OpenAI call timed out (transport-level retry exhausted); retrying in {Delay}s (attempt {Attempt}/{Max})",
                    delay.TotalSeconds, attempt, MaxTransientRetries);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsTimeoutAggregate(AggregateException ex) =>
        ex.InnerExceptions.Any(inner => inner is TaskCanceledException or TimeoutException
            || inner is IOException
            || (inner is System.Net.Sockets.SocketException));

    private static bool IsTransient(int status) =>
        status == 429 || status == 408 || status >= 500;

    private static TimeSpan? RetryAfter(ClientResultException ex)
    {
        var response = ex.GetRawResponse();
        if (response is null) return null;

        if (response.Headers.TryGetValue("Retry-After", out var value)
            && int.TryParse(value, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, 60));
        }

        return null;
    }
}
