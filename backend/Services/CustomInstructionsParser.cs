using System.Linq;
using System.Text.RegularExpressions;

namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// Pulls a small set of deterministic directives out of the free-text "processing instructions"
/// box, for the handful of things that must happen before the LLM ever sees the file — restricting
/// which sheet gets read chief among them. Everything else in that box still goes to the mapper
/// verbatim; this only intercepts patterns explicit enough to act on safely without an LLM call.
/// </summary>
public static partial class CustomInstructionsParser
{
    // Matches an explicit INCLUSION directive — "read the sheet ...", with or without
    // "named"/"called" between "sheet" and the name:
    //   only read the sheet named "Commissions"
    //   read only the sheet called Commissions
    //   only read the sheet "8215 Sales"
    [GeneratedRegex(
        """read\s+(?:only\s+)?the\s+sheet(?:\s+(?:named|called))?\s+["“']?(?<name>[^"”'\n,.;]+)["”']?""",
        RegexOptions.IgnoreCase)]
    private static partial Regex SheetReadDirectiveRegex();

    // "sheet: Commissions" / "sheet name: Commissions"
    [GeneratedRegex(
        """sheet\s*(?:name)?\s*[:=]\s*["“']?(?<name>[^"”'\n,.;]+)["”']?""",
        RegexOptions.IgnoreCase)]
    private static partial Regex SheetColonDirectiveRegex();

    // Loosest fallback — quoted text immediately followed by the word "sheet", e.g. "only use the
    // "Commissions" sheet". Guarded against matching an EXCLUSION instead ("ignore the "X" sheet",
    // "not the "X" sheet") via the negative lookbehind, since that phrasing names the sheet to skip,
    // not the one to keep — see the real bug this guards against in TryExtractSheetFilter's doc.
    [GeneratedRegex(
        """(?<!ignor(?:e|ing)\s)(?<!not\s)(?<!skip\s)(?<!except\s)["“'](?<name>[^"”']+)["”']\s+sheet\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex SheetQuotedFallbackRegex();

    /// <summary>
    /// Returns the sheet name to restrict extraction to, or null if the instructions don't name one.
    ///
    /// Checked in priority order — an explicit "read the sheet ..." always wins over the generic
    /// quoted-text-then-"sheet" fallback. That fallback alone previously matched WHICHEVER sheet
    /// name happened to be quoted-then-followed-by-"sheet" first in the text, including one named in
    /// an exclusion clause: "Only read the sheet "8215 Sales"; ignore the "summary" sheet" locked
    /// onto "summary" — the sheet the operator explicitly wanted excluded — because "summary" sheet"
    /// matches that pattern and "sheet "8215 Sales"" (name comes AFTER "sheet", no colon) didn't
    /// match any pattern at all. Real bug, observed in production logs.
    /// </summary>
    public static string? TryExtractSheetFilter(string? customInstructions)
    {
        if (string.IsNullOrWhiteSpace(customInstructions)) return null;

        var name = SheetReadDirectiveRegex().Match(customInstructions) is { Success: true } m1 ? m1.Groups["name"].Value
            : SheetColonDirectiveRegex().Match(customInstructions) is { Success: true } m2 ? m2.Groups["name"].Value
            : SheetQuotedFallbackRegex().Match(customInstructions) is { Success: true } m3 ? m3.Groups["name"].Value
            : null;

        name = name?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    // Captures the list-of-names span after "read the sheets ...", up to the first sentence
    // boundary (an em/en dash, a period followed by space/end, or a semicolon) so a trailing
    // exclusion clause ("— ignore ...") never becomes part of the list.
    [GeneratedRegex(
        """read\s+(?:only\s+)?the\s+sheets\s+(?<list>.+?)(?:\s*[—–]|\s*--|\.(?:\s|$)|;)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex SheetsReadDirectiveRegex();

    private static readonly Regex QuotedNameRegex = new("""["“'](?<name>[^"”']+)["”']""", RegexOptions.Compiled);

    /// <summary>
    /// Returns every sheet name a "read the sheets "A", "B" and "C"" (plural) directive lists, or
    /// null if the instructions don't use that plural form — see <see cref="TryExtractSheetFilter"/>
    /// for the singular case. A document naming several sheets this way previously fell through to
    /// no deterministic filtering at all (the singular-only parser never matched "sheets"), leaving
    /// the model to guess sheet inclusion/exclusion from free text alone across dozens of chunks —
    /// unreliable exactly the way pinning a single money column was before it became deterministic.
    /// </summary>
    public static List<string>? TryExtractSheetFilters(string? customInstructions)
    {
        if (string.IsNullOrWhiteSpace(customInstructions)) return null;

        var listMatch = SheetsReadDirectiveRegex().Match(customInstructions);
        if (!listMatch.Success) return null;

        var names = QuotedNameRegex.Matches(listMatch.Groups["list"].Value)
            .Select(m => m.Groups["name"].Value.Trim())
            .Where(n => n.Length > 0)
            .ToList();

        return names.Count > 0 ? names : null;
    }

    // Matches things like:
    //   use only the money column whose header starts with "Current Month Gross Sales"
    //   only use the column that starts with "Current Month Gross Sales"
    [GeneratedRegex(
        """column[^"“'\n]*(?:starts?\s+with|begins?\s+with)\s+["“'](?<prefix>[^"”'\n]+)["”']""",
        RegexOptions.IgnoreCase)]
    private static partial Regex MoneyColumnPrefixRegex();

    // Matches the exact-name form, without "starts with":
    //   use the column "2026 Invoiced price" as netSales
    //   use "2026 Invoiced price" as netSales
    // Deliberately NOT anchored to the start of the instructions, and independent from the
    // per-sheet "on sheet(s) ... use ... as netSales" clauses above — a document can have a single
    // global one of these, several per-sheet ones, or (harmlessly) both, since the per-sheet map
    // always wins for tables it names and this is only the fallback for the rest.
    [GeneratedRegex(
        """use\s+(?:the\s+column\s+)?["“'](?<prefix>[^"”'\n]+)["”']\s+as\s+netsales""",
        RegexOptions.IgnoreCase)]
    private static partial Regex MoneyColumnExactRegex();

    /// <summary>
    /// Returns the header prefix/name a "use ... as netSales" directive names — either the
    /// "starts with" form or the exact-name form — or null if the instructions don't pin one.
    /// Callers use this to physically remove every other money-like column before the LLM ever sees
    /// the table — see the caller for why a text instruction alone isn't reliable enough on
    /// documents with dozens of chunks.
    /// </summary>
    public static string? TryExtractMoneyColumnPrefix(string? customInstructions)
    {
        if (string.IsNullOrWhiteSpace(customInstructions)) return null;

        var match = MoneyColumnPrefixRegex().Match(customInstructions);
        if (!match.Success) match = MoneyColumnExactRegex().Match(customInstructions);
        if (!match.Success) return null;

        var prefix = match.Groups["prefix"].Value.Trim();
        return prefix.Length > 0 ? prefix : null;
    }

    // Matches "On sheet(s) "A"[, "B"] use ["]COLUMN["] as netSales" clauses — repeatable, so a
    // document with several sheet groups each needing a different money column (e.g. a workbook
    // with FEI/Tigris/TDP tabs per product line, each using a differently-named "commissionable
    // sales" column) can pin ALL of them, not just one global column across the whole file.
    [GeneratedRegex(
        """on\s+sheets?\s+(?<names>(?:["“'][^"”'\n]+["”']\s*(?:,\s*|and\s+)?)+)\s*use\s+(?:the\s+column\s+)?["“'](?<col>[^"”'\n]+)["”']\s+as\s+netsales""",
        RegexOptions.IgnoreCase)]
    private static partial Regex PerSheetMoneyColumnRegex();

    /// <summary>
    /// Returns a sheet-name → column-prefix map for every "On sheet(s) ... use "COLUMN" as
    /// netSales" clause in the instructions, or null if none are present. Callers apply each
    /// sheet's own pinned column instead of one global one — see
    /// <see cref="TryExtractMoneyColumnPrefix"/> for the single-column case this generalizes.
    /// </summary>
    public static Dictionary<string, string>? TryExtractPerSheetMoneyColumnPrefixes(string? customInstructions)
    {
        if (string.IsNullOrWhiteSpace(customInstructions)) return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match clause in PerSheetMoneyColumnRegex().Matches(customInstructions))
        {
            var col = clause.Groups["col"].Value.Trim();
            if (col.Length == 0) continue;

            foreach (Match nameMatch in QuotedNameRegex.Matches(clause.Groups["names"].Value))
            {
                var sheet = nameMatch.Groups["name"].Value.Trim();
                if (sheet.Length > 0) map[sheet] = col;
            }
        }

        return map.Count > 0 ? map : null;
    }

    // "On sheet "BUILD" only include rows where "month" is "1"." — also accepts "equals"/"=" in
    // place of "is". Built for workbooks that mix several periods' rows into one tab, where a
    // column meant to scope the sheet to the report's own period (e.g. a numeric "month" column)
    // carries a handful of stray entries from another period — see MarkdownTableNormalizer.RowFilter.
    // The negative lookahead keeps this from swallowing the "is not blank" form below as a literal
    // value of "not blank".
    [GeneratedRegex(
        """on\s+sheet\s+["“'](?<sheet>[^"”'\n]+)["”']\s+only\s+include\s+rows\s+where\s+["“'](?<col>[^"”'\n]+)["”']\s+(?:is(?!\s+not\b)|equals|=)\s+["“']?(?<val>[^"”'\n.]+?)["”']?\s*[.\n]""",
        RegexOptions.IgnoreCase)]
    private static partial Regex RowFilterRegex();

    // "On sheet "Sheet1" only include rows where "Item Code" is not blank." — for reports whose
    // aggregate/subtotal rows carry no text label at all (every identifying column blank, only the
    // numeric ones populated), so there is no keyword for a total-row detector to key off — see
    // MarkdownTableNormalizer.RowFilter.
    [GeneratedRegex(
        """on\s+sheet\s+["“'](?<sheet>[^"”'\n]+)["”']\s+only\s+include\s+rows\s+where\s+["“'](?<col>[^"”'\n]+)["”']\s+is\s+not\s+(?:blank|empty)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex RowFilterNotBlankRegex();

    /// <summary>
    /// Returns a sheet-name → (column, value) row filter for every "On sheet "X" only include rows
    /// where "COLUMN" is "VALUE"" (or "... is not blank") clause in the instructions, or null if
    /// none are present.
    /// </summary>
    internal static Dictionary<string, MarkdownTableNormalizer.RowFilter>? TryExtractRowFilters(string? customInstructions)
    {
        if (string.IsNullOrWhiteSpace(customInstructions)) return null;

        var map = new Dictionary<string, MarkdownTableNormalizer.RowFilter>(StringComparer.OrdinalIgnoreCase);

        foreach (Match clause in RowFilterNotBlankRegex().Matches(customInstructions))
        {
            var sheet = clause.Groups["sheet"].Value.Trim();
            var col = clause.Groups["col"].Value.Trim();
            if (sheet.Length == 0 || col.Length == 0) continue;

            map[sheet] = new MarkdownTableNormalizer.RowFilter(col, null);
        }

        foreach (Match clause in RowFilterRegex().Matches(customInstructions))
        {
            var sheet = clause.Groups["sheet"].Value.Trim();
            var col = clause.Groups["col"].Value.Trim();
            var val = clause.Groups["val"].Value.Trim();
            if (sheet.Length == 0 || col.Length == 0 || val.Length == 0) continue;

            map[sheet] = new MarkdownTableNormalizer.RowFilter(col, val);
        }

        return map.Count > 0 ? map : null;
    }
}
