namespace ManufacturerExtraction.Api.Services.Interfaces;

public interface IContentUnderstandingService
{
    Task<string> SubmitAndPollAsync(string blobUrl, CancellationToken ct = default);
}