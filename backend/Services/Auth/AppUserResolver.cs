using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Services.Auth;

/// <summary>
/// Turns the validated Entra token into the application's own user. Who may sign in at all is decided
/// in Microsoft Entra (the Enterprise application's user assignment), so anyone holding a valid token
/// is welcome here: the first time a person appears, the application creates their account and their
/// own client — named after them — on the spot. Returns null only for a token that identifies no one.
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
            // First time this person is seen: create their account (and their own client).
            user = await ProvisionAsync(oid, emails, name, ct);
            if (user is null) return null;
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

    /// <summary>
    /// Creates the account for a person seen for the first time. A bootstrap admin joins the default
    /// client as an admin; everyone else gets a new client carrying their own name, so the "Client"
    /// column of the queue shows who uploaded what and each person keeps a private manufacturer list.
    /// </summary>
    private async Task<AppUser?> ProvisionAsync(string? oid, List<string> emails, string? name, CancellationToken ct)
    {
        var bootstrap = emails.FirstOrDefault(e => _auth.BootstrapAdminEmails.Contains(e));
        var email = bootstrap ?? emails.FirstOrDefault() ?? (oid is null ? null : $"{oid}@entra");
        if (email is null) return null;

        var displayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        Client client;
        if (bootstrap is not null)
        {
            client = await _db.Clients.FirstAsync(c => c.Id == Client.DefaultId, ct);
        }
        else
        {
            var clientName = displayName ?? email;
            // Two people can share a display name; client names are unique, so tell them apart by email.
            if (await _db.Clients.AnyAsync(c => c.Name.ToLower() == clientName.ToLower(), ct))
                clientName = $"{clientName} ({email})";
            client = new Client { Id = Guid.NewGuid(), Name = clientName, CreatedDate = DateTime.UtcNow };
            _db.Clients.Add(client);
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(), Email = email, ObjectId = oid, DisplayName = displayName,
            ClientId = client.Id, Client = client, IsAdmin = bootstrap is not null, CreatedDate = DateTime.UtcNow,
        };
        _db.AppUsers.Add(user);

        try
        {
            await _db.SaveChangesAsync(ct);
            return user;
        }
        catch (DbUpdateException)
        {
            // The browser fires several requests at once on first load: a parallel one created the same
            // account a moment earlier (unique email). Use theirs instead of failing this request.
            _db.ChangeTracker.Clear();
            return (oid is not null ? await _db.AppUsers.Include(u => u.Client).FirstOrDefaultAsync(u => u.ObjectId == oid, ct) : null)
                   ?? await _db.AppUsers.Include(u => u.Client).FirstOrDefaultAsync(u => u.Email == email, ct);
        }
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
