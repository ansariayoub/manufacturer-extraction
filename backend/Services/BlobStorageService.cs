using Azure.Storage.Blobs;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Services;

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _containerClient;

    public BlobStorageService(IConfiguration config)
{
    // Read from the standard ASP.NET Core "ConnectionStrings" section (config.GetConnectionString)
    // rather than a custom "AzureBlobStorage:ConnectionString" key. This is required, not just a
    // style choice: Azure App Service's "Application settings" blade rejects any setting whose name
    // ends in "ConnectionString" (it collides with the platform's dedicated "Connection strings"
    // feature). Using the ConnectionStrings section lets the value be set there instead, exactly
    // like ConnectionStrings:AzureSql already is.
    var connectionString = config.GetConnectionString("AzureBlobStorage")
        ?? throw new InvalidOperationException("Blob storage connection string missing");
    var containerName = config["AzureBlobStorage:ContainerName"] ?? "documents";

    var options = new BlobClientOptions
    {
        Retry =
        {
            NetworkTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 5,
            Delay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(30)
        }
    };

    var serviceClient = new BlobServiceClient(connectionString, options);
    _containerClient = serviceClient.GetBlobContainerClient(containerName);
    _containerClient.CreateIfNotExists();
}
    public async Task<string> UploadAsync(Stream fileStream, string fileName, string contentType)
    {
        var blobName = $"{Guid.NewGuid()}-{fileName}";
        var blobClient = _containerClient.GetBlobClient(blobName);

        await blobClient.UploadAsync(fileStream, new Azure.Storage.Blobs.Models.BlobHttpHeaders
        {
            ContentType = contentType
        });

        return blobClient.Uri.ToString();
    }

    public async Task<Stream> DownloadAsync(string blobUrl)
{
    var blobName = Uri.UnescapeDataString(new Uri(blobUrl).Segments[^1]);
    var blobClient = _containerClient.GetBlobClient(blobName);
    var response = await blobClient.DownloadStreamingAsync();
    return response.Value.Content;
}
public Task<string> GenerateSasUrlAsync(string blobUrl, TimeSpan validFor)
{
    var blobName = Uri.UnescapeDataString(new Uri(blobUrl).Segments[^1]);
    var blobClient = _containerClient.GetBlobClient(blobName);

    if (!blobClient.CanGenerateSasUri)
        throw new InvalidOperationException("Cannot generate SAS URI - check storage account authentication method.");

    var sasBuilder = new Azure.Storage.Sas.BlobSasBuilder
    {
        BlobContainerName = _containerClient.Name,
        BlobName = blobName,
        Resource = "b",
        ExpiresOn = DateTimeOffset.UtcNow.Add(validFor)
    };
    sasBuilder.SetPermissions(Azure.Storage.Sas.BlobSasPermissions.Read);

    var sasUri = blobClient.GenerateSasUri(sasBuilder);
    return Task.FromResult(sasUri.ToString());
}
}