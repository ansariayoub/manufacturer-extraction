using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Dtos;
using ManufacturerExtraction.Api.Models;
using ManufacturerExtraction.Api.Services.Auth;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Controllers;

/// <summary>
/// The viewer's heavy content, served one page at a time. Opening a document used to download its
/// whole raw extraction AND every canonical line in one response and render them all at once, which
/// froze the browser on large files. Now each tab asks only for the slice it is showing, and the
/// parsed form is kept briefly in memory so paging forward does not re-read the database blob.
/// </summary>
[ApiController]
[Route("api/documents/{id:guid}")]
public class DocumentContentController : ControllerBase
{
    private const int MaxPageSize = 2000;
    private const int ExtractedPageChars = 60_000;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ICumulativePeriodService _cumulativePeriod;
    private readonly CurrentUserAccessor _currentUser;
    private readonly ILogger<DocumentContentController> _logger;

    public DocumentContentController(
        AppDbContext db, IMemoryCache cache, ICumulativePeriodService cumulativePeriod, CurrentUserAccessor currentUser,
        ILogger<DocumentContentController> logger)
    {
        _logger = logger;
        _db = db;
        _cache = cache;
        _cumulativePeriod = cumulativePeriod;
        _currentUser = currentUser;
    }

    private AppUserContext Me => _currentUser.Current;

    // ------------------------------------------------------------------ canonical lines

    [HttpGet("canonical")]
    public async Task<ActionResult<CanonicalPageDto>> Canonical(
        Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 500, CancellationToken ct = default)
    {
        var (document, lines) = await LoadCanonicalAsync(id, ct);
        if (document is null) return NotFound();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 50, MaxPageSize);
        lines ??= new();

        var slice = lines.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        // Totals cover the WHOLE document, not just the page on screen, so the footer still matches
        // the figure shown in the queue.
        var net = lines.Sum(l => (decimal?)l.NetSales ?? 0m);
        var commission = lines.Sum(l => (decimal?)l.Commission ?? 0m);

        return Ok(new CanonicalPageDto(slice, page, pageSize, lines.Count, net, commission));
    }

    [HttpGet("canonical.csv")]
    public async Task<IActionResult> CanonicalCsv(Guid id, CancellationToken ct)
    {
        var (document, lines) = await LoadCanonicalAsync(id, ct);
        if (document is null) return NotFound();

        static string Field(string? s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        static string Num(decimal? v) => v?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";

        var sb = new StringBuilder();
        sb.Append("Customer ID,Customer,Date,City,State,Product family,Part no,Qty,Net sales,Commission\r\n");
        foreach (var l in lines ?? new())
        {
            sb.Append(string.Join(',',
                Field(l.CustomerId), Field(l.CustomerName), Field(l.Date?.ToString("yyyy-MM-dd")),
                Field(l.City), Field(l.State), Field(l.ProductFamily), Field(l.PartNo),
                Num(l.Quantity), Num((decimal?)l.NetSales), Num((decimal?)l.Commission))).Append("\r\n");
        }

        // Leading BOM so Excel opens accented/UTF-8 content correctly instead of guessing a codepage.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
        return File(bytes, "text/csv; charset=utf-8", $"{BaseName(document)}.canonical.csv");
    }

    // ------------------------------------------------------------------ raw extraction

    [HttpGet("extracted")]
    public async Task<ActionResult<ExtractedPageDto>> Extracted(
        Guid id, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var (document, raw) = await LoadRawAsync(id, ct);
        if (document is null) return NotFound();

        var text = raw?.Pretty ?? "";
        var pageCount = Math.Max(1, (int)Math.Ceiling(text.Length / (double)ExtractedPageChars));
        page = Math.Clamp(page, 1, pageCount);

        var start = (page - 1) * ExtractedPageChars;
        var length = Math.Min(ExtractedPageChars, text.Length - start);
        return Ok(new ExtractedPageDto(length > 0 ? text.Substring(start, length) : "", page, pageCount, text.Length));
    }

    [HttpGet("extracted/download")]
    public async Task<IActionResult> ExtractedDownload(Guid id, CancellationToken ct)
    {
        var (document, raw) = await LoadRawAsync(id, ct);
        if (document is null || raw is null) return NotFound();

        return File(Encoding.UTF8.GetBytes(raw.Pretty), "application/json", $"{BaseName(document)}.extraction.json");
    }

    // ------------------------------------------------------------------ source workbook

    [HttpGet("source")]
    public async Task<ActionResult<SourcePageDto>> Source(
        Guid id, [FromQuery] int sheet = 0, [FromQuery] int page = 1, [FromQuery] int pageSize = 300, CancellationToken ct = default)
    {
        var (document, raw) = await LoadRawAsync(id, ct);
        if (document is null) return NotFound();

        // A PDF has no worksheet to show — the viewer embeds the original instead.
        var sheets = document.FileType == ".pdf" ? new List<SourceSheet>() : raw?.Sheets ?? new List<SourceSheet>();
        var infos = sheets.Select(s => new SourceSheetInfoDto(s.Name, s.Rows.Count)).ToList();
        if (sheets.Count == 0)
            return Ok(new SourcePageDto(infos, 0, new(), new(), 1, pageSize, 0));

        sheet = Math.Clamp(sheet, 0, sheets.Count - 1);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 50, MaxPageSize);

        var current = sheets[sheet];
        var rows = current.Rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Ok(new SourcePageDto(infos, sheet, current.Headers, rows, page, pageSize, current.Rows.Count));
    }

    // ------------------------------------------------------------------ loading + caching

    private static string BaseName(Document d) => Path.GetFileNameWithoutExtension(d.OriginalFileName);

    private Task<Document?> FindVisibleAsync(Guid id, CancellationToken ct) =>
        _db.Documents.AsNoTracking().VisibleTo(Me).FirstOrDefaultAsync(d => d.Id == id, ct);

    private void Remember<T>(string key, T value, int approxChars) =>
        _cache.Set(key, value, new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, approxChars) * 2L,
            SlidingExpiration = CacheLifetime,
        });

    private async Task<(Document?, List<AnalyticsTransaction>?)> LoadCanonicalAsync(Guid id, CancellationToken ct)
    {
        var document = await FindVisibleAsync(id, ct);
        if (document is null) return (null, null);

        var extractionId = await _db.AnalyticsExtractions
            .Where(a => a.DocumentId == id).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        if (extractionId is null) return (document, null);

        var key = $"canon:{extractionId}";
        if (_cache.TryGetValue(key, out List<AnalyticsTransaction>? cached)) return (document, cached);

        var clock = Stopwatch.StartNew();
        var json = await _db.AnalyticsExtractions
            .Where(a => a.Id == extractionId).Select(a => a.AnalyticsJson).FirstAsync(ct);
        var readMs = clock.ElapsedMilliseconds;
        var report = JsonSerializer.Deserialize<AnalyticsReport>(json, ReadOptions);
        var parseMs = clock.ElapsedMilliseconds - readMs;

        // For a cumulative report the month's own lines are the ones worth showing.
        var lines = report?.MonthlySales ?? report?.Sales ?? new();
        if (document.IsCumulative)
        {
            var period = await _cumulativePeriod.ComputeMonthlyAsync(document, lines, ct);
            if (period.MonthlyLines is not null) lines = period.MonthlyLines;
        }

        _logger.LogInformation(
            "Canonical for {Id}: {Lines} line(s), {Chars:N0} chars — database read {Read} ms, parse {Parse} ms, cumulative step {Rest} ms",
            id, lines.Count, json.Length, readMs, parseMs, clock.ElapsedMilliseconds - readMs - parseMs);

        Remember(key, lines, json.Length);
        return (document, lines);
    }

    /// <summary>What the Extracted and Input file tabs both derive from the one stored raw extraction.</summary>
    private sealed record RawView(string Pretty, List<SourceSheet> Sheets);

    /// <summary>
    /// Reads the raw extraction from the database ONCE and derives both views from it. It is the
    /// largest blob a document has, so the Extracted and Input file tabs must not each pay for
    /// their own read of it.
    /// </summary>
    private async Task<(Document?, RawView?)> LoadRawAsync(Guid id, CancellationToken ct)
    {
        var document = await FindVisibleAsync(id, ct);
        if (document is null) return (null, null);

        var rawId = await _db.RawExtractions
            .Where(r => r.DocumentId == id).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (rawId is null) return (document, null);

        var key = $"raw:{rawId}";
        if (_cache.TryGetValue(key, out RawView? cached)) return (document, cached);

        var clock = Stopwatch.StartNew();
        var raw = await _db.RawExtractions.Where(r => r.Id == rawId).Select(r => r.RawJson).FirstAsync(ct);
        var readMs = clock.ElapsedMilliseconds;
        var view = new RawView(Pretty(raw), ParseSheets(raw));

        _logger.LogInformation(
            "Raw extraction for {Id}: {Chars:N0} chars — database read {Read} ms, derive {Derive} ms",
            id, raw.Length, readMs, clock.ElapsedMilliseconds - readMs);

        // Pretty text plus the parsed rows: roughly two copies of the raw size.
        Remember(key, view, raw.Length * 2);
        return (document, view);
    }

    /// <summary>Re-indents valid JSON; anything that is not valid JSON is returned untouched.</summary>
    private static string Pretty(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                doc.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private sealed record SourceSheet(string Name, List<string> Headers, List<List<string>> Rows);

    /// <summary>
    /// Reads the per-sheet markdown tables SpreadsheetExtractionService wrote into the raw JSON
    /// (<c>result.contents[].markdown</c>) back into rows, so the Input file tab can show the real
    /// workbook content before any normalization or model mapping touched it.
    /// </summary>
    private static List<SourceSheet> ParseSheets(string rawJson)
    {
        var sheets = new List<SourceSheet>();
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("contents", out var contents)
                || contents.ValueKind != JsonValueKind.Array)
                return sheets;

            foreach (var content in contents.EnumerateArray())
            {
                if (!content.TryGetProperty("markdown", out var md) || md.ValueKind != JsonValueKind.String) continue;

                var name = content.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "Sheet" : "Sheet";
                var headers = new List<string>();
                var rows = new List<List<string>>();
                var headerSeen = false;

                foreach (var rawLine in md.GetString()!.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("# ")) { name = line[2..].Trim(); continue; }
                    if (!line.StartsWith('|')) continue;

                    var cells = SplitRow(line);
                    if (cells.All(c => c.Length == 0 || c.All(ch => ch == '-'))) continue; // "| --- | --- |"
                    if (!headerSeen) { headers = cells; headerSeen = true; continue; }
                    rows.Add(cells);
                }

                if (headers.Count > 0) sheets.Add(new SourceSheet(name, headers, rows));
            }
        }
        catch (JsonException)
        {
            // Not a spreadsheet-reader payload; the tab falls back to "open the original".
        }
        return sheets;
    }

    /// <summary>Splits "| a | b \| c |" into ["a", "b | c"], honouring the \| escape the writer uses.</summary>
    private static List<string> SplitRow(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var end = line.EndsWith('|') && !line.EndsWith("\\|") ? line.Length - 1 : line.Length;

        for (var i = 1; i < end; i++)
        {
            var ch = line[i];
            if (ch == '\\' && i + 1 < end && line[i + 1] == '|') { current.Append('|'); i++; }
            else if (ch == '|') { cells.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(ch);
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }
}
