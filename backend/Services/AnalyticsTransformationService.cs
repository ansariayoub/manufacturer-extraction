using System.Collections.Concurrent;
using System.ClientModel;
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
    private const int TargetRowsPerChunk = 200;

    // Floor for the split-on-truncation recursion. Below this many rows we stop splitting and
    // record a warning instead of looping forever.
    private const int MinRowsToSplit = 4;

    // Per-document concurrency. Kept modest because several documents are now processed in
    // parallel by the background worker pool as well — the product of the two is what Azure sees.
    private const int MaxConcurrentChunksPerDocument = 3;

    // Retries per model call for transient Azure failures (429 throttling above all).
    private const int MaxTransientRetries = 5;

    // Sheet-level context (report title, period, commission rates) repeated in every chunk.
    private const int MaxPreambleChars = 2_000;

    public AnalyticsTransformationService(IConfiguration config, ILogger<AnalyticsTransformationService> logger)
    {
        _logger = logger;

        var endpoint = config["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("Azure OpenAI endpoint missing");
        var apiKey = config["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("Azure OpenAI API key missing");
        var deployment = config["AzureOpenAI:DeploymentName"]
            ?? throw new InvalidOperationException("Azure OpenAI deployment name missing");

        var clientOptions = new AzureOpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromMinutes(10)
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
    private static string BuildSystemPrompt(string? customInstructions, string? columnPlan = null)
    {
        var prompt = BaseSystemPrompt;

        // The column map goes before the user overrides so that operator instructions still win.
        if (!string.IsNullOrWhiteSpace(columnPlan))
            prompt = $"{prompt}\n\n{columnPlan}";

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
    internal static List<Chunk> BuildChunks(List<TableParts> tables, int targetRowsPerChunk)
    {
        var chunks = new List<Chunk>();

        for (int t = 0; t < tables.Count; t++)
        {
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
        }

        return chunks;
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
        // Excel serial dates converted exactly, aggregate rows removed, float noise stripped.
        // Everything this does was previously left to the model to do per row — and it drifted.
        var normalized = MarkdownTableNormalizer.Normalize(rawMarkdown);
        _logger.LogInformation(
            "Markdown normalized: {Columns} duplicate/empty column(s) removed, {Totals} aggregate row(s) dropped",
            normalized.DroppedColumns, normalized.DroppedTotalRows);

        var tables = SplitTables(normalized.Markdown);
        var chunks = BuildChunks(tables, TargetRowsPerChunk);

        // One cheap call decides the column mapping for the whole document, before any row is
        // extracted. Every chunk then receives that same decision as a binding constraint.
        var columnPlan = await BuildColumnPlanAsync(
            tables, customInstructions, fallbackManufacturer, fallbackPeriodMonth, fallbackPeriodYear, ct);

        var systemPrompt = BuildSystemPrompt(customInstructions, columnPlan);

        var results = new List<AnalyticsTransaction>?[chunks.Count];
        var warnings = new ConcurrentBag<string>();
        var completed = 0;
        var throttle = new SemaphoreSlim(MaxConcurrentChunksPerDocument);
        var progressLock = new SemaphoreSlim(1, 1);

        var tasks = chunks.Select(async (chunk, i) =>
        {
            await throttle.WaitAsync(ct);
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
                throttle.Release();
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

        var rowsSent = chunks.Sum(c => c.RowCount);
        var rowsReturned = allSales.Count;

        return new TransformationResult(
            new AnalyticsReport { Sales = allSales },
            warnings.OrderBy(w => w).ToList(),
            rowsSent,
            rowsReturned);
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
        }
    }

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
