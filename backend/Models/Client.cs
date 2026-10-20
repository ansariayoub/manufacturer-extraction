namespace ManufacturerExtraction.Api.Models;

/// <summary>
/// A customer company using the platform. Every manufacturer list, prompt, and uploaded report
/// belongs to exactly one client, and a signed-in user only ever sees their own client's data
/// (unless they are an admin, who sees every client's documents).
/// </summary>
public class Client
{
    /// <summary>
    /// Fixed id of the client that owns everything created before multi-client support existed.
    /// The migration seeds this row and attaches all pre-existing manufacturers and documents to
    /// it, so nothing already in the database is orphaned or lost.
    /// </summary>
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-00000000c001");

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
}
