using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Services.Auth;

/// <summary>
/// Turns the validated Entra token into the application's own user: looks the person up in the
/// allow-list table and returns their client and role, or null when they are not on the list.
/// </summary>
public class AppUserResolver
{
    private readonly AppDbContext _db;
    private readonly AuthSettings _auth;

    public AppUserResolver(AppDbContext db, AuthSettings auth)
    {
        _db = db;
        _auth = auth;
    }

    public async Task<AppUserContext?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!_auth.Enabled)
        {
            // Sign-in not configured: behave as before it existed.
            var defaultClient = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == Client.DefaultId, ct);
            return new AppUserContext(Guid.Empty, "local@dev", "Local development", defaultClient.Id, defaultClient.Name, IsAdmin: true);
        }

        // v1 and v2 Entra tokens name the same facts with different claim types, and the JWT handler
        // may or may not have remapped them to the long .NET names — accept every spelling.
        var oid = First(principal, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier");
        var emails = new[]
            {
                First(principal, "preferred_username"), First(principal, "email", ClaimTypes.Email),
                First(principal, "upn", ClaimTypes.Upn), First(principal, "unique_name"),
            }
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        var name = First(principal, "name", ClaimTypes.Name);

        if (oid is null && emails.Count == 0) return null;

        var user = (oid is not null ? await _db.AppUsers.Include(u => u.Client).FirstOrDefaultAsync(u => u.ObjectId == oid, ct) : null)
                   ?? await _db.AppUsers.Include(u => u.Client).FirstOrDefaultAsync(u => emails.Contains(u.Email), ct);

        if (user is null)
        {
            // First administrator: admitted by configuration instead of by a database row.
            var bootstrap = emails.FirstOrDefault(e => _auth.BootstrapAdminEmails.Contains(e));
            if (bootstrap is null) return null;

            user = new AppUser
            {
                Id = Guid.NewGuid(),
                Email = bootstrap,
                ClientId = Client.DefaultId,
                IsAdmin = true,
                CreatedDate = DateTime.UtcNow,
            };
            _db.AppUsers.Add(user);
            await _db.SaveChangesAsync(ct);
            user.Client = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == Client.DefaultId, ct);
        }

        // Remember the stable identifiers the first time we see them (rows are usually created from
        // an email alone, before the person has ever signed in).
        var changed = false;
        if (oid is not null && user.ObjectId != oid) { user.ObjectId = oid; changed = true; }
        if (!string.IsNullOrWhiteSpace(name) && user.DisplayName != name) { user.DisplayName = name; changed = true; }
        if (changed) await _db.SaveChangesAsync(ct);

        return new AppUserContext(
            user.Id, user.Email, user.DisplayName ?? user.Email,
            user.ClientId, user.Client?.Name ?? "", user.IsAdmin);
    }

    private static string? First(ClaimsPrincipal p, params string[] types)
    {
        foreach (var type in types)
        {
            var value = p.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }
}
