using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Dtos;
using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Controllers;

/// <summary>
/// Backs the admin Settings page's manufacturer manager. The manufacturer list used to be a
/// hardcoded frontend array — adding a manufacturer not on it meant a code change and a deploy.
/// This makes the list (and each manufacturer's default processing-instructions template) editable
/// data instead, so an operator can add one the moment a new manufacturer's reports show up.
/// </summary>
[ApiController]
[Route("api/manufacturers")]
public class ManufacturersController : ControllerBase
{
    private readonly AppDbContext _db;

    public ManufacturersController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<List<ManufacturerDto>>> List(CancellationToken ct)
    {
        var manufacturers = await _db.Manufacturers
            .OrderBy(m => m.Name)
            .Select(m => new ManufacturerDto(m.Id, m.Name, m.DefaultInstructions, m.CreatedDate))
            .ToListAsync(ct);
        return Ok(manufacturers);
    }

    [HttpPost]
    public async Task<ActionResult<ManufacturerDto>> Create(CreateManufacturerRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest("Manufacturer name is required.");

        var exists = await _db.Manufacturers.AnyAsync(m => m.Name.ToLower() == name.ToLower(), ct);
        if (exists)
            return Conflict($"A manufacturer named \"{name}\" already exists.");

        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = name,
            DefaultInstructions = string.IsNullOrWhiteSpace(request.DefaultInstructions) ? null : request.DefaultInstructions.Trim(),
            CreatedDate = DateTime.UtcNow,
        };
        _db.Manufacturers.Add(manufacturer);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(List), new { },
            new ManufacturerDto(manufacturer.Id, manufacturer.Name, manufacturer.DefaultInstructions, manufacturer.CreatedDate));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ManufacturerDto>> Update(Guid id, UpdateManufacturerRequest request, CancellationToken ct)
    {
        var manufacturer = await _db.Manufacturers.FindAsync(new object?[] { id }, ct);
        if (manufacturer is null) return NotFound();

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest("Manufacturer name cannot be blank.");

            var duplicate = await _db.Manufacturers.AnyAsync(m => m.Id != id && m.Name.ToLower() == name.ToLower(), ct);
            if (duplicate)
                return Conflict($"A manufacturer named \"{name}\" already exists.");

            manufacturer.Name = name;
        }

        // Distinguishing "field omitted" from "field cleared to empty" matters here: the request
        // DTO's DefaultInstructions is only touched when the caller explicitly included it, so a
        // rename-only PUT (DefaultInstructions absent) never wipes out an existing default.
        if (request.DefaultInstructions is not null)
        {
            var newInstructions = string.IsNullOrWhiteSpace(request.DefaultInstructions)
                ? null
                : request.DefaultInstructions.Trim();

            // Snapshot the OUTGOING value before it's overwritten — history is the trail of what a
            // manufacturer's default USED to be, so there is nothing to record the first time a
            // prompt is set (nothing existed before it) or when saving the exact same text again.
            if (!string.IsNullOrWhiteSpace(manufacturer.DefaultInstructions)
                && manufacturer.DefaultInstructions != newInstructions)
            {
                _db.ManufacturerPromptHistory.Add(new ManufacturerPromptHistory
                {
                    Id = Guid.NewGuid(),
                    ManufacturerId = manufacturer.Id,
                    Instructions = manufacturer.DefaultInstructions,
                    CreatedDate = DateTime.UtcNow,
                });
            }

            manufacturer.DefaultInstructions = newInstructions;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new ManufacturerDto(manufacturer.Id, manufacturer.Name, manufacturer.DefaultInstructions, manufacturer.CreatedDate));
    }

    /// <summary>Past default-prompt versions for one manufacturer, newest first.</summary>
    [HttpGet("{id:guid}/prompt-history")]
    public async Task<ActionResult<List<ManufacturerPromptHistoryDto>>> GetPromptHistory(Guid id, CancellationToken ct)
    {
        var exists = await _db.Manufacturers.AnyAsync(m => m.Id == id, ct);
        if (!exists) return NotFound();

        var history = await _db.ManufacturerPromptHistory
            .Where(h => h.ManufacturerId == id)
            .OrderByDescending(h => h.CreatedDate)
            .Select(h => new ManufacturerPromptHistoryDto(h.Id, h.Instructions, h.CreatedDate))
            .ToListAsync(ct);
        return Ok(history);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var manufacturer = await _db.Manufacturers.FindAsync(new object?[] { id }, ct);
        if (manufacturer is null) return NotFound();

        // Deliberately NOT blocked by existing documents referencing this name: Document.Manufacturer
        // is a plain string snapshot taken at upload time, not a foreign key, so removing the
        // manufacturer from the picker never orphans or breaks an already-uploaded document.
        _db.Manufacturers.Remove(manufacturer);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
