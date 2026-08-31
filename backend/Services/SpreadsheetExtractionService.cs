using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExcelDataReader;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// Reads .xlsx / .xls workbooks directly, cell by cell, instead of sending them to Azure AI
/// Content Understanding.
///
/// Two reasons, both observed on real files in this project:
///
/// 1. Content Understanding cannot read every workbook. A minimally-written .xlsx (no
///    docProps/app.xml, no sharedStrings.xml, no theme) is sniffed as "application/zip" and comes
///    back containing nothing but the sheet name — the document was then silently marked complete
///    with zero lines. 23 of the 953 test workbooks are in that state.
///
/// 2. Even when it works, going through document understanding loses fidelity that has to be
///    reconstructed downstream: dates arrive as raw Excel serial numbers, merged cells become
///    duplicated columns, the real header row can end up looking like data, and floats carry
///    binary noise. Reading the cells gives all of that back exactly, for free.
///
/// The output deliberately mimics the Content Understanding response shape, so the rest of the
/// pipeline — table splitting, chunking, the column plan, the "Extracted" viewer — is unchanged.
/// PDFs still go through Content Understanding, which is what it is good at.
/// </summary>
public class SpreadsheetExtractionService : ISpreadsheetExtractionService
{
    private readonly ILogger<SpreadsheetExtractionService> _logger;

    static SpreadsheetExtractionService()
    {
        // Required by ExcelDataReader to read legacy .xls, which uses code-page encodings that
        // .NET Core does not register by default.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public SpreadsheetExtractionService(ILogger<SpreadsheetExtractionService> logger) => _logger = logger;

    public async Task<string> ExtractAsync(Stream fileStream, string fileName, CancellationToken ct, IReadOnlyCollection<string>? onlySheetNames = null)
    {
        // Blob downloads hand back a forward-only network stream, but both the zip (.xlsx) and
        // the BIFF (.xls) readers need to seek. Buffer first.
        Stream seekable = fileStream;
        MemoryStream? buffer = null;
        if (!fileStream.CanSeek)
        {
            buffer = new MemoryStream();
            await fileStream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            seekable = buffer;
        }

        try
        {
            return Extract(seekable, fileName, ct, onlySheetNames);
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    private string Extract(Stream fileStream, string fileName, CancellationToken ct, IReadOnlyCollection<string>? onlySheetNames)
    {
        using var reader = ExcelReaderFactory.CreateReader(fileStream);

        var contents = new List<object>();
        var totalRows = 0;
        var skippedSheets = new List<string>();
        var wantedSheets = onlySheetNames?.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        do
        {
            ct.ThrowIfCancellationRequested();

            var sheetName = string.IsNullOrWhiteSpace(reader.Name) ? $"Sheet{contents.Count + 1}" : reader.Name;

            // Skip the whole sheet up front — not just its rows downstream — so a workbook with
            // several large unrelated tabs doesn't pay to read, store, and chunk data nobody asked
            // for. reader.Read() below still has to be called to advance past this sheet's rows.
            var wanted = wantedSheets is null || wantedSheets.Contains(sheetName.Trim());
            if (!wanted)
            {
                skippedSheets.Add(sheetName);
                while (reader.Read()) { }
                continue;
            }

            var rows = new List<List<string>>();

            while (reader.Read())
            {
                var cells = new List<string>(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                    cells.Add(FormatCell(reader.GetValue(i)));

                if (cells.Any(c => c.Length > 0)) rows.Add(cells);
            }

            if (rows.Count == 0) continue;

            // SAP BEx exports (Geberit and others) carry a few sheets that are pure metadata, never
            // transaction data: "SEnn" holds the query's own selection criteria ("Selektion" as its
            // first cell), and "TXnn" holds a text/code translation lookup (its first cell reads
            // "BI-Query mit Texten..."), typically thousands of rows of unrelated codes and labels.
            // Neither has anything resembling a netSales column, so the deterministic pinned-column
            // safety net has nothing to check them against — a model that fabricates "sales" rows
            // out of these tables (observed on a real Geberit file: totals in the billions) sails
            // straight through unguarded. They are identified by content, not sheet name, since
            // naming isn't consistent across exports.
            // The signature cell isn't always the very first row — TX-style sheets lead with a
            // generic "Info" row before the one that actually names the sheet — so check the first
            // couple of rows rather than only rows[0].
            var isMetadataSheet = rows.Take(2)
                .SelectMany(r => r)
                .Any(c => c.Equals("Selektion", StringComparison.OrdinalIgnoreCase)
                    || c.StartsWith("BI-Query mit Texten", StringComparison.OrdinalIgnoreCase));
            if (isMetadataSheet)
            {
                skippedSheets.Add(sheetName);
                continue;
            }

            // A sheet where every single row has at most one populated cell is a stray list of
            // bare values (IDs, pasted numbers), never a transaction table — this app's sales data
            // always needs multiple fields (customer, amount, ...) on the same row. Observed on a
            // real IPS Sales file: a leftover "Feuil1" tab holding five bare numbers with no header
            // at all, which still has nothing for the deterministic pinned-column safety net to
            // check it against, so a model that read anything into it as "sales" went unguarded —
            // inflating that document's total by tens of thousands of dollars.
            if (rows.All(r => r.Count(c => c.Length > 0) <= 1))
            {
                skippedSheets.Add(sheetName);
                continue;
            }

            // A sheet where every populated cell parses as a plain number, with no header row and no
            // identifying text anywhere (no customer, product, or label column), is never real
            // transaction data — a genuine sales table always carries at least one text column.
            // Observed on a real Rheem Sales file: a leftover "Feuil1" tab holding two rows of bare
            // numbers (e.g. "8195283 | 1906775"), which turned out to be an internal QA checksum
            // pairing a row count with that month's already-known grand total — not sales rows at
            // all. With no header for the deterministic pinned-column safety net to check it
            // against, a model that read its numbers as "sales" inflated that document's total by
            // millions.
            if (rows.All(r => r.All(c => c.Length == 0 || double.TryParse(c, NumberStyles.Any, CultureInfo.InvariantCulture, out _))))
            {
                skippedSheets.Add(sheetName);
                continue;
            }

            // A sheet whose NAME reads as reference material ("Territory Lookup", "Notes", ...) and
            // that carries no money-shaped column anywhere in its first few rows is a lookup/glossary
            // table, not sales data — observed on a real Raypak file ("Territory Lookup": City,
            // Territory, State, County, nothing resembling an amount). Requiring BOTH the suspicious
            // name AND the absence of anything money-shaped keeps this from skipping a sheet that
            // merely happens to be named unusually but still holds real transactions — a sheet named
            // "Notes" that also has an "Amount" column is left alone.
            var sheetNameSuggestsReference = Regex.IsMatch(sheetName,
                @"\b(notes?|read\s*me|instructions?|lookup|glossary|legend|definitions?)\b",
                RegexOptions.IgnoreCase);
            if (sheetNameSuggestsReference)
            {
                var hasMoneyLikeHeader = rows.Take(5).SelectMany(r => r).Any(c => Regex.IsMatch(c,
                    @"\b(sales|amount|amt|total|price|cost|commission|revenue|value|invoice|paid)\b",
                    RegexOptions.IgnoreCase));
                if (!hasMoneyLikeHeader)
                {
                    skippedSheets.Add(sheetName);
                    continue;
                }
            }

            TrimTrailingEmptyColumns(rows);
            totalRows += rows.Count;

            contents.Add(new
            {
                path = sheetName,
                markdown = BuildMarkdown(sheetName, rows),
                kind = "worksheet",
                rowCount = rows.Count
            });
        }
        while (reader.NextResult());

        if (wantedSheets is not null && contents.Count == 0)
        {
            // None of the requested sheet names matched anything — fail open rather than silently
            // returning an empty document. skippedSheets lists what the workbook actually has, so
            // the error is directly actionable (usually a typo or slightly different sheet name).
            throw new InvalidOperationException(
                $"No sheet named {string.Join(" or ", wantedSheets.Select(n => $"'{n}'"))} was found in '{fileName}'. " +
                $"Sheets present: {string.Join(", ", skippedSheets)}.");
        }

        _logger.LogInformation(
            "Read {File} directly: {Sheets} sheet(s), {Rows} non-empty row(s){Skipped}",
            fileName, contents.Count, totalRows,
            skippedSheets.Count > 0 ? $" — skipped {skippedSheets.Count}: {string.Join(", ", skippedSheets)}" : "");

        if (contents.Count == 0)
            throw new InvalidOperationException($"The workbook '{fileName}' contains no readable rows.");

        var payload = new
        {
            status = "Succeeded",
            source = "spreadsheet-reader",
            result = new
            {
                analyzerId = "local-spreadsheet-reader",
                createdAt = DateTime.UtcNow,
                contents
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// Emits the sheet as a markdown table. The first non-empty row is used as the header, which
    /// is correct here because we are reading real cells — there is no page furniture to confuse
    /// it with, unlike the document-understanding output.
    /// </summary>
    private static string BuildMarkdown(string sheetName, List<List<string>> rows)
    {
        var width = rows.Max(r => r.Count);
        foreach (var r in rows)
            while (r.Count < width) r.Add("");

        var sb = new StringBuilder();
        sb.AppendLine($"# {sheetName}").AppendLine();

        var header = rows[0];
        sb.AppendLine("| " + string.Join(" | ", header.Select(EscapePipes)) + " |");
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", width)));

        foreach (var row in rows.Skip(1))
            sb.AppendLine("| " + string.Join(" | ", row.Select(EscapePipes)) + " |");

        return sb.ToString();
    }

    private static string EscapePipes(string cell) => cell.Replace("|", "\\|");

    private static void TrimTrailingEmptyColumns(List<List<string>> rows)
    {
        var width = rows.Max(r => r.Count);
        var lastUsed = -1;

        for (int c = 0; c < width; c++)
            if (rows.Any(r => c < r.Count && r[c].Length > 0))
                lastUsed = c;

        foreach (var r in rows)
            if (r.Count > lastUsed + 1)
                r.RemoveRange(lastUsed + 1, r.Count - lastUsed - 1);
    }

    /// <summary>
    /// Renders one cell. Dates come out already in ISO form — the single change that removes the
    /// Excel-serial arithmetic the model used to do by hand, and get wrong, on every row.
    /// </summary>
    private static string FormatCell(object? value) => value switch
    {
        null => "",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),

        // 4 decimals strips binary noise (280.16000000000003) while keeping genuine rates intact.
        double d => Math.Round(d, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture),
        decimal m => Math.Round(m, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture),
        float f => Math.Round((double)f, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture),

        bool b => b ? "TRUE" : "FALSE",
        _ => value.ToString()?.Trim().Replace("\r", " ").Replace("\n", " ") ?? ""
    };
}
