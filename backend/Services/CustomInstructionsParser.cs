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
    // Matches things like:
    //   only read the sheet named "Commissions"
    //   read only the sheet called Commissions
    //   sheet: Commissions
    //   only use the "Commissions" sheet
    [GeneratedRegex(
        """(?:sheet\s*(?:name)?\s*[:=]\s*["“']?(?<name1>[^"”'\n,.]+)["”']?)|(?:sheet\s+(?:named|called)\s+["“']?(?<name2>[^"”'\n,.]+)["”']?)|(?:["“'](?<name3>[^"”']+)["”']\s+sheet\b)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex SheetDirectiveRegex();

    /// <summary>
    /// Returns the sheet name to restrict extraction to, or null if the instructions don't name one.
    /// </summary>
    public static string? TryExtractSheetFilter(string? customInstructions)
    {
        if (string.IsNullOrWhiteSpace(customInstructions)) return null;

        var match = SheetDirectiveRegex().Match(customInstructions);
        if (!match.Success) return null;

        var name = match.Groups["name1"].Success ? match.Groups["name1"].Value
            : match.Groups["name2"].Success ? match.Groups["name2"].Value
            : match.Groups["name3"].Value;

        name = name.Trim();
        return name.Length > 0 ? name : null;
    }
}
