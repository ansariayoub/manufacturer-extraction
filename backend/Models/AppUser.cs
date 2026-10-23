namespace ManufacturerExtraction.Api.Models;

/// <summary>
/// A person who uses the application. Microsoft Entra authenticates who someone is and, through the
/// Enterprise application's "assignment required" setting, decides who may sign in at all. The row is
/// created automatically the first time the person signs in, together with their own client; it
/// records which client they belong to and whether they are an admin.
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
