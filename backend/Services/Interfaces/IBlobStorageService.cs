namespace ManufacturerExtraction.Api.Services.Interfaces;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream fileStream, string fileName, string contentType);
    Task<Stream> DownloadAsync(string blobUrl);
    Task<string> GenerateSasUrlAsync(string blobUrl, TimeSpan validFor);
}