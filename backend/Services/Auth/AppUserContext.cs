using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Services.Auth;

/// <summary>Who is calling, resolved once per request: their allow-list row plus the client it belongs to.</summary>
public record AppUserContext(Guid UserId, string Email, string DisplayName, Guid ClientId, string ClientName, bool IsAdmin);

/// <summary>Filled in by <see cref="AppUserFilter"/> before any controller action runs.</summary>
public class CurrentUserAccessor
{
    private AppUserContext? _current;

    public AppUserContext Current =>
        _current ?? throw new InvalidOperationException("No signed-in user has been resolved for this request.");

    public void Set(AppUserContext context) => _current = context;
}

public static class TenantScopeExtensions
{
    /// <summary>Documents the caller may see: their own client's, or every client's for an admin.</summary>
    public static IQueryable<Document> VisibleTo(this IQueryable<Document> query, AppUserContext user) =>
        user.IsAdmin ? query : query.Where(d => d.ClientId == user.ClientId);
}
