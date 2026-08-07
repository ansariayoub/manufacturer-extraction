namespace ManufacturerExtraction.Api.Models;

public class RawExtraction
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string RawJson { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }

    public Document Document { get; set; } = null!;
}