using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Dtos;
using ManufacturerExtraction.Api.Models;
using ManufacturerExtraction.Api.Services.Auth;

namespace ManufacturerExtraction.Api.Controllers;

/// <summary>
/// Admin-only management of clients and users. Accounts are created automatically on a person's first
/// Microsoft sign-in (Entra's Enterprise application decides who may sign in at all); this is where an
/// admin renames a client, moves a person to another client, or makes someone an admin.
/// </summary>
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CurrentUserAccessor _currentUser;

    public AdminController(AppDbContext db, CurrentUserAccessor currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    private bool IsAdmin => _currentUser.Current.IsAdmin;

    // ------------------------------------------------------------------ clients

    [HttpGet("clients")]
    public async Task<ActionResult<List<ClientDto>>> ListClients(CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        var clients = await _db.Clients
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new ClientDto(c.Id, c.Name, _db.AppUsers.Count(u => u.ClientId == c.Id), c.CreatedDate))
            .ToListAsync(ct);
        return Ok(clients);
    }

    [HttpPost("clients")]
    public async Task<ActionResult<ClientDto>> CreateClient(CreateClientRequest request, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("Client name is required.");
        if (await _db.Clients.AnyAsync(c => c.Name.ToLower() == name.ToLower(), ct))
            return Conflict($"A client named \"{name}\" already exists.");

        var client = new Client { Id = Guid.NewGuid(), Name = name, CreatedDate = DateTime.UtcNow };
        _db.Clients.Add(client);
        await _db.SaveChangesAsync(ct);
        return Ok(new ClientDto(client.Id, client.Name, 0, client.CreatedDate));
    }

    [HttpPut("clients/{id:guid}")]
    public async Task<ActionResult<ClientDto>> RenameClient(Guid id, RenameClientRequest request, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        var client = await _db.Clients.FindAsync(new object?[] { id }, ct);
        if (client is null) return NotFound();

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("Client name is required.");
        if (await _db.Clients.AnyAsync(c => c.Id != id && c.Name.ToLower() == name.ToLower(), ct))
            return Conflict($"A client named \"{name}\" already exists.");

        client.Name = name;
        await _db.SaveChangesAsync(ct);
        var users = await _db.AppUsers.CountAsync(u => u.ClientId == id, ct);
        return Ok(new ClientDto(client.Id, client.Name, users, client.CreatedDate));
    }

    [HttpDelete("clients/{id:guid}")]
    public async Task<IActionResult> DeleteClient(Guid id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        if (id == Client.DefaultId) return Conflict("The default client cannot be deleted.");

        var client = await _db.Clients.FindAsync(new object?[] { id }, ct);
        if (client is null) return NotFound();

        // A client that still owns people, manufacturers or reports must not vanish with them.
        if (await _db.AppUsers.AnyAsync(u => u.ClientId == id, ct)
            || await _db.Manufacturers.AnyAsync(m => m.ClientId == id, ct)
            || await _db.Documents.AnyAsync(d => d.ClientId == id, ct))
            return Conflict("This client still has users, manufacturers or documents — remove or move them first.");

        _db.Clients.Remove(client);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ------------------------------------------------------------------ users

    [HttpGet("users")]
    public async Task<ActionResult<List<AppUserDto>>> ListUsers(CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        var users = await _db.AppUsers
            .AsNoTracking()
            .OrderBy(u => u.Client!.Name).ThenBy(u => u.Email)
            .Select(u => new AppUserDto(u.Id, u.Email, u.DisplayName, u.ClientId, u.Client!.Name, u.IsAdmin, u.ObjectId != null))
            .ToListAsync(ct);
        return Ok(users);
    }

    [HttpPost("users")]
    public async Task<ActionResult<AppUserDto>> CreateUser(CreateAppUserRequest request, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        var email = request.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return BadRequest("A valid email address is required.");
        if (await _db.AppUsers.AnyAsync(u => u.Email == email, ct))
            return Conflict($"{email} is already on the list.");

        var client = await _db.Clients.FindAsync(new object?[] { request.ClientId }, ct);
        if (client is null) return BadRequest("Unknown client.");

        var user = new AppUser
        {
            Id = Guid.NewGuid(), Email = email, ClientId = client.Id,
            IsAdmin = request.IsAdmin, CreatedDate = DateTime.UtcNow,
        };
        _db.AppUsers.Add(user);
        await _db.SaveChangesAsync(ct);
        return Ok(new AppUserDto(user.Id, user.Email, user.DisplayName, client.Id, client.Name, user.IsAdmin, false));
    }

    [HttpPut("users/{id:guid}")]
    public async Task<ActionResult<AppUserDto>> UpdateUser(Guid id, UpdateAppUserRequest request, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        var user = await _db.AppUsers.Include(u => u.Client).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        if (request.ClientId is { } clientId && clientId != user.ClientId)
        {
            var client = await _db.Clients.FindAsync(new object?[] { clientId }, ct);
            if (client is null) return BadRequest("Unknown client.");
            user.ClientId = client.Id;
            user.Client = client;
        }

        if (request.IsAdmin is { } isAdmin)
        {
            // Keep at least one admin so nobody can lock everyone out of the admin screens.
            if (!isAdmin && user.IsAdmin && await _db.AppUsers.CountAsync(u => u.IsAdmin, ct) <= 1)
                return Conflict("There must always be at least one admin.");
            user.IsAdmin = isAdmin;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new AppUserDto(user.Id, user.Email, user.DisplayName, user.ClientId, user.Client?.Name ?? "", user.IsAdmin, user.ObjectId != null));
    }

    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        if (id == _currentUser.Current.UserId) return Conflict("You cannot remove your own access.");

        var user = await _db.AppUsers.FindAsync(new object?[] { id }, ct);
        if (user is null) return NotFound();

        if (user.IsAdmin && await _db.AppUsers.CountAsync(u => u.IsAdmin, ct) <= 1)
            return Conflict("There must always be at least one admin.");

        _db.AppUsers.Remove(user);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
