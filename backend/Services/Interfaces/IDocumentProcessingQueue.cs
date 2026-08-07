namespace ManufacturerExtraction.Api.Services.Interfaces;

public interface IDocumentProcessingQueue
{
    /// <summary>Adds a document to the processing queue. Returns immediately.</summary>
    void Enqueue(Guid documentId);

    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}
