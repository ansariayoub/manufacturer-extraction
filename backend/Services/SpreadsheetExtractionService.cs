using System.Globalization;
using System.Text;
using System.Text.Json;
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

    public async Task<string> ExtractAsync(Stream fileStream, string fileName, CancellationToken ct)
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
            return Extract(seekable, fileName, ct);
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    private string Extract(Stream fileStream, string fileName, CancellationToken ct)
    {
        using var reader = ExcelReaderFactory.CreateReader(fileStream);

        var contents = new List<object>();
        var totalRows = 0;

        do
        {
            ct.ThrowIfCancellationRequested();

            var sheetName = string.IsNullOrWhiteSpace(reader.Name) ? $"Sheet{contents.Count + 1}" : reader.Name;
            var rows = new List<List<string>>();

            while (reader.Read())
            {
                var cells = new List<string>(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                    cells.Add(FormatCell(reader.GetValue(i)));

                if (cells.Any(c => c.Length > 0)) rows.Add(cells);
            }

            if (rows.Count == 0) continue;

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

        _logger.LogInformation(
            "Read {File} directly: {Sheets} sheet(s), {Rows} non-empty row(s)",
            fileName, contents.Count, totalRows);

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
