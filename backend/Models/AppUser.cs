namespace ManufacturerExtraction.Api.Models;

/// <summary>
/// An allow-listed person. Microsoft Entra authenticates who someone is (and the Enterprise
/// application's "assignment required" setting gates who may sign in at all); this table is the
/// application's own second check — which client the person belongs to, and whether they are an
/// admin. A signed-in Entra user with no row here is refused with 403.
/// </summary>
public class AppUser
{
    public Guid Id { get; set; }

    /// <summary>Entra object id. Unknown until the person's first sign-in, when it is filled in.</summary>
    public string? ObjectId { get; set; }

    /// <summary>Sign-in address, stored lowercase; used to recognise the person before their object id is known.</summary>
    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public Guid ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>Admins see every client's documents and may change the AI model and manage clients/users.</summary>
    public bool IsAdmin { get; set; }

    public DateTime CreatedDate { get; set; }
}
