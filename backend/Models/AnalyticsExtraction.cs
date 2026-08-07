namespace ManufacturerExtraction.Api.Models;

public class AnalyticsExtraction
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string AnalyticsJson { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = "v1";
    public DateTime CreatedDate { get; set; }

    public Document Document { get; set; } = null!;
}