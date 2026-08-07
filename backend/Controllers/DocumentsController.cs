using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Dtos;
using ManufacturerExtraction.Api.Models;
using ManufacturerExtraction.Api.Services;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Controllers;

[ApiController]
[Route("api/documents")]
public class DocumentsController : ControllerBase
{
    private static readonly string[] AllowedExtensions = { ".xlsx", ".xls", ".pdf" };

    private readonly AppDbContext _db;
    private readonly IBlobStorageService _blobStorage;
    private readonly IDocumentProcessingQueue _queue;
    private readonly IProcessingCancellationRegistry _cancellationRegistry;
    private readonly ICumulativePeriodService _cumulativePeriod;

    public DocumentsController(
        AppDbContext db,
        IBlobStorageService blobStorage,
        IDocumentProcessingQueue queue,
        IProcessingCancellationRegistry cancellationRegistry,
        ICumulativePeriodService cumulativePeriod)
    {
        _db = db;
        _blobStorage = blobStorage;
        _queue = queue;
        _cancellationRegistry = cancellationRegistry;
        _cumulativePeriod = cumulativePeriod;
    }

    [HttpPost("upload")]
    public async Task<ActionResult<DocumentSummaryDto>> Upload(
        IFormFile file,
        [FromForm] string manufacturer,
        [FromForm] string periodMonth,
        [FromForm] string periodYear,
        [FromForm] string? customInstructions,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No file uploaded.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
            return BadRequest($"Unsupported file type '{extension}'. Allowed: {string.Join(", ", AllowedExtensions)}");

        await using var stream = file.OpenReadStream();
        var blobUrl = await _blobStorage.UploadAsync(stream, file.FileName, file.ContentType);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            OriginalFileName = file.FileName,
            BlobUrl = blobUrl,
            FileType = extension,
            FileSizeBytes = file.Length,
            UploadDate = DateTime.UtcNow,
            ProcessingStatus = ProcessingStatus.Queued,
            Manufacturer = manufacturer,
            PeriodMonth = periodMonth,
            PeriodYear = periodYear,
            CustomInstructions = string.IsNullOrWhiteSpace(customInstructions) ? null : customInstructions
        };

        _db.Documents.Add(document);
        await _db.SaveChangesAsync(ct);

        return Ok(document.ToSummaryDto());
    }

    /// <summary>
    /// Hands the document to the background worker pool and returns straight away. Processing is
    /// no longer started inline with Task.Run — see DocumentProcessingQueue for why.
    /// </summary>
    [HttpPost("{id:guid}/analyze")]
    public async Task<ActionResult> Analyze(Guid id, CancellationToken ct)
    {
        var exists = await _db.Documents.AnyAsync(d => d.Id == id, ct);
        if (!exists) return NotFound();

        _queue.Enqueue(id);
        return Accepted();
    }

    /// <summary>
    /// Re-runs the canonical mapping for an already-processed document, optionally with new
    /// custom instructions. The stored RawExtraction is reused when present, so this does not
    /// re-run Content Understanding — it is fast, free, and lets the user iterate on their
    /// instructions without re-uploading the file.
    /// </summary>
    [HttpPost("{id:guid}/reanalyze")]
    public async Task<ActionResult<DocumentSummaryDto>> Reanalyze(
        Guid id, [FromBody] ReanalyzeRequest? request, CancellationToken ct)
    {
        var document = await _db.Documents
            .Include(d => d.AnalyticsExtraction)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (document is null) return NotFound();

        if (request is not null)
        {
            document.CustomInstructions = string.IsNullOrWhiteSpace(request.CustomInstructions)
                ? null
                : request.CustomInstructions;
        }

        // Drop the previous canonical output — the new run replaces it. RawExtraction is kept
        // on purpose: it is the permanent source of truth and lets us skip Content Understanding.
        if (document.AnalyticsExtraction is not null)
            _db.AnalyticsExtractions.Remove(document.AnalyticsExtraction);

        document.ProcessingStatus = ProcessingStatus.Queued;
        document.ProgressPct = 0;
        document.ErrorMessage = null;
        document.HasWarnings = false;
        document.TotalNetSales = null;
        document.TotalCommission = null;
        document.LineCount = null;
        document.CustomerCount = null;
        document.AnalysisCompletedDate = null;

        await _db.SaveChangesAsync(ct);

        _queue.Enqueue(id);
        return Ok(document.ToSummaryDto());
    }

    /// <summary>
    /// Full queue listing. Projected in SQL so the nvarchar(max) JSON columns are never read:
    /// this endpoint is polled, and loading them was costing seconds and tens of megabytes per call.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<DocumentSummaryDto>>> GetAll(CancellationToken ct)
    {
        // The anonymous type keeps the enum as-is (ToString() has no reliable SQL translation);
        // the DTO is then built in memory. The important part is the explicit column list —
        // RawJson / AnalyticsJson are never part of the query.
        var rows = await _db.Documents
            .AsNoTracking()
            .OrderByDescending(d => d.UploadDate)
            .Select(d => new
            {
                d.Id, d.OriginalFileName, d.FileSizeBytes, d.UploadDate,
                d.Manufacturer, d.PeriodMonth, d.PeriodYear,
                d.ProcessingStatus, d.ProgressPct, d.ErrorMessage, d.HasWarnings,
                d.TotalNetSales, d.TotalCommission,
                d.LineCount, d.CustomerCount, d.CustomInstructions, d.IsCumulative
            })
            .ToListAsync(ct);

        // Year-to-date reports carry a total that accumulates from January. The month's own figure
        // is derived here, at read time, by subtracting the previous period's document — pure
        // arithmetic over columns already in hand, so it adds no query and no JSON parsing.
        // Deriving it here rather than during processing means each file is still processed
        // entirely on its own, and upload order does not matter.
        var totalsByPeriod = rows
            .Where(r => r.IsCumulative && r.TotalNetSales != null)
            .GroupBy(r => (r.Manufacturer, r.PeriodYear, r.PeriodMonth))
            .ToDictionary(g => g.Key, g => g.First());

        var documents = rows.Select(d =>
        {
            decimal? prevNet = null, prevComm = null;

            var previous = CumulativePeriodService.PreviousPeriod(d.PeriodMonth, d.PeriodYear);
            if (d.IsCumulative && previous is not null
                && totalsByPeriod.TryGetValue((d.Manufacturer, previous.Value.Year, previous.Value.Month), out var prev))
            {
                prevNet = prev.TotalNetSales;
                prevComm = prev.TotalCommission;
            }

            return new DocumentSummaryDto(
                d.Id, d.OriginalFileName, d.FileSizeBytes, d.UploadDate,
                d.Manufacturer, d.PeriodMonth, d.PeriodYear,
                d.ProcessingStatus.ToString(), d.ProgressPct, d.ErrorMessage, d.HasWarnings,
                d.TotalNetSales, d.TotalCommission,
                d.LineCount, d.CustomerCount, d.CustomInstructions,
                d.IsCumulative,
                CumulativePeriodService.DeriveMonthly(d.IsCumulative, d.PeriodMonth, d.TotalNetSales, prevNet),
                CumulativePeriodService.DeriveMonthly(d.IsCumulative, d.PeriodMonth, d.TotalCommission, prevComm),
                null);
        }).ToList();

        return Ok(documents);
    }

    /// <summary>
    /// Progress-only listing used by the UI's polling loop. A few small columns per row, so it
    /// stays fast enough to be called every second while a batch is running.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<List<DocumentStatusDto>>> GetStatuses(CancellationToken ct)
    {
        var rows = await _db.Documents
            .AsNoTracking()
            .Select(d => new { d.Id, d.ProcessingStatus, d.ProgressPct, d.ErrorMessage, d.HasWarnings })
            .ToListAsync(ct);

        var statuses = rows.Select(d => new DocumentStatusDto(
            d.Id, d.ProcessingStatus.ToString(), d.ProgressPct, d.ErrorMessage, d.HasWarnings)).ToList();

        return Ok(statuses);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DocumentDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        var document = await _db.Documents
            .AsNoTracking()
            .Include(d => d.RawExtraction)
            .Include(d => d.AnalyticsExtraction)
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (document is null) return NotFound();

        string? sourceUrl = null;
        try
        {
            sourceUrl = await _blobStorage.GenerateSasUrlAsync(document.BlobUrl, TimeSpan.FromMinutes(10));
        }
        catch { }

        var dto = document.ToDetailDto(sourceUrl);

        // For a cumulative report, the viewer shows the month's own lines rather than the
        // year-to-date ones. This is the single-document path, so loading the previous month's
        // canonical JSON to diff against is affordable here — unlike on the polled listing.
        if (document.IsCumulative)
        {
            var period = await _cumulativePeriod.ComputeMonthlyAsync(document, dto.CanonicalRecords, ct);
            if (period.MonthlyLines is not null)
            {
                dto = dto with
                {
                    CanonicalRecords = period.MonthlyLines,
                    MonthlyNetSales = period.MonthlyLines.Sum(l => (decimal?)l.NetSales ?? 0m),
                    MonthlyCommission = period.MonthlyLines.Sum(l => (decimal?)l.Commission ?? 0m),
                    MonthlyLineCount = period.MonthlyLines.Count
                };
            }
        }

        return Ok(dto);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        _cancellationRegistry.Cancel(id); // stoppe net tout traitement en cours pour ce document

        var document = await _db.Documents.FindAsync(new object[] { id }, ct);
        if (document is null) return NotFound();

        _db.Documents.Remove(document);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
