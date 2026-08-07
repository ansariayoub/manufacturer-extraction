namespace ManufacturerExtraction.Api.Services.Interfaces;

public interface IProcessingCancellationRegistry
{
    CancellationTokenSource Register(Guid documentId);
    void Cancel(Guid documentId);
    void Remove(Guid documentId);
}