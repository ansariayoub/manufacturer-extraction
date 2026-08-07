namespace ManufacturerExtraction.Api.Services.Interfaces;

public interface IDocumentProcessingService
{
    Task ProcessAsync(Guid documentId, CancellationToken ct = default);
}