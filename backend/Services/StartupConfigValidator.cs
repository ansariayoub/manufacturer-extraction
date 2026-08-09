using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;

namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// Validates every required configuration value once, right after the host is built, before it
/// starts accepting requests.
///
/// This exists because of a recurring, hard-to-diagnose failure mode: a bad connection string
/// (wrong format, stale rotated key, leftover placeholder) doesn't stop the app from starting —
/// Kestrel binds the port, "Now listening on..." prints, everything LOOKS fine. The failure only
/// surfaces later, per request, buried in a 40-line Azure SDK stack trace, on whichever endpoint
/// happens to touch the broken dependency first (e.g. Blob Storage is only ever constructed
/// lazily, the first time a controller needing it is hit). Compounding that: a stale `dotnet run`
/// left over from before a fix was applied keeps answering requests with its old, broken config,
/// making it look like the fix didn't work at all.
///
/// Checking everything up front turns both failure modes into the same one: the process refuses
/// to start, with a short, specific message and no stack trace. A stale process can no longer look
/// alive-but-broken — either it started successfully with valid config (in which case config is
/// not the problem), or it never started at all.
/// </summary>
public static class StartupConfigValidator
{
    public static void ValidateOrExit(IConfiguration config, ILogger logger)
    {
        var problems = new List<string>();

        var sqlConn = config.GetConnectionString("AzureSql");
        CheckPresent(problems, "ConnectionStrings:AzureSql", sqlConn);
        if (!string.IsNullOrWhiteSpace(sqlConn))
        {
            try { _ = new SqlConnectionStringBuilder(sqlConn); }
            catch (Exception ex)
            {
                problems.Add($"ConnectionStrings:AzureSql is not a valid SQL connection string ({ex.Message}).");
            }
        }

        var blobConn = config.GetConnectionString("AzureBlobStorage");
        CheckPresent(problems, "ConnectionStrings:AzureBlobStorage", blobConn);
        if (!string.IsNullOrWhiteSpace(blobConn))
        {
            // Constructing the client is enough to trigger the SDK's own parsing — the same code
            // path BlobStorageService uses — without making any network call.
            try { _ = new BlobServiceClient(blobConn); }
            catch (Exception ex)
            {
                problems.Add($"ConnectionStrings:AzureBlobStorage is not a valid storage connection string ({ex.Message}). " +
                              "Expected format: DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net");
            }
        }

        CheckPresent(problems, "AzureContentUnderstanding:Endpoint", config["AzureContentUnderstanding:Endpoint"]);
        CheckPresent(problems, "AzureContentUnderstanding:ApiKey", config["AzureContentUnderstanding:ApiKey"]);
        CheckPresent(problems, "AzureOpenAI:Endpoint", config["AzureOpenAI:Endpoint"]);
        CheckPresent(problems, "AzureOpenAI:ApiKey", config["AzureOpenAI:ApiKey"]);
        CheckPresent(problems, "AzureOpenAI:DeploymentName", config["AzureOpenAI:DeploymentName"]);

        if (problems.Count > 0)
        {
            logger.LogCritical(
                "Startup aborted — {Count} configuration problem(s) found:\n  - {Problems}\n" +
                "Fix locally with 'dotnet user-secrets set \"<Key>\" \"<value>\"' from the backend folder, " +
                "or in Azure via Key Vault / Application settings, then restart.",
                problems.Count, string.Join("\n  - ", problems));

            Environment.Exit(1);
        }

        // Masked confirmation banner: printed on every successful start, so if several terminal
        // windows are open it is immediately obvious — without making a request — which one is
        // actually serving the current configuration.
        logger.LogInformation(
            "Configuration OK — SQL server: {SqlServer}, Blob account: {BlobAccount}, AI endpoint: {AiEndpoint}",
            ExtractSqlServer(sqlConn), ExtractAccountName(blobConn), config["AzureOpenAI:Endpoint"]);
    }

    private static void CheckPresent(List<string> problems, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('<'))
            problems.Add($"{key} is missing or still set to its placeholder value.");
    }

    private static string ExtractSqlServer(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "(none)";
        try { return new SqlConnectionStringBuilder(connectionString).DataSource; }
        catch { return "(unparsable)"; }
    }

    private static string ExtractAccountName(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "(none)";
        try { return new BlobServiceClient(connectionString).AccountName; }
        catch { return "(unparsable)"; }
    }
}
