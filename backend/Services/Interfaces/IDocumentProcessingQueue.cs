namespace ManufacturerExtraction.Api.Services.Interfaces;

public interface IDocumentProcessingQueue
{
    /// <summary>
    /// Adds a document to the processing queue. Returns immediately. A no-op if the document is
    /// already queued or currently being processed — see DocumentProcessingQueue for why.
    /// </summary>
    void Enqueue(Guid documentId);

    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);

    /// <summary>Marks a document as no longer queued/processing. Called by the worker when it finishes.</summary>
    void MarkFinished(Guid documentId);
}
