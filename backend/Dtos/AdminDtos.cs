namespace ManufacturerExtraction.Api.Dtos;

/// <summary>The signed-in caller, as returned by GET /api/me.</summary>
public record MeDto(string Email, string DisplayName, Guid ClientId, string ClientName, bool IsAdmin, bool AuthEnabled);

public record ClientDto(Guid Id, string Name, int UserCount, DateTime CreatedDate);
public record CreateClientRequest(string Name);
public record RenameClientRequest(string Name);

public record AppUserDto(Guid Id, string Email, string? DisplayName, Guid ClientId, string ClientName, bool IsAdmin, bool HasSignedIn);
public record CreateAppUserRequest(string Email, Guid ClientId, bool IsAdmin);
public record UpdateAppUserRequest(Guid? ClientId, bool? IsAdmin);
