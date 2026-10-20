namespace ManufacturerExtraction.Api.Services.Auth;

/// <summary>
/// Whether Microsoft Entra sign-in is enforced, read once at startup from the "AzureAd" section.
/// When ClientId/TenantId are not configured the API stays open and treats every caller as an admin
/// of the default client — the same behaviour as before sign-in existed — so the code can ship
/// before the Azure app registration is created without taking the app offline.
/// </summary>
public class AuthSettings
{
    public bool Enabled { get; }
    public string TenantId { get; }
    public string ClientId { get; }

    /// <summary>
    /// Emails that are admitted as admins of the default client the first time they sign in, so the
    /// very first administrator never has to be inserted into the database by hand.
    /// </summary>
    public IReadOnlyList<string> BootstrapAdminEmails { get; }

    public AuthSettings(IConfiguration config)
    {
        TenantId = config["AzureAd:TenantId"]?.Trim() ?? "";
        ClientId = config["AzureAd:ClientId"]?.Trim() ?? "";

        // Placeholder values like "<tenant-id>" in the template appsettings count as "not configured".
        static bool Real(string v) => !string.IsNullOrWhiteSpace(v) && !v.StartsWith('<');
        Enabled = Real(TenantId) && Real(ClientId);

        BootstrapAdminEmails = (config.GetSection("Auth:BootstrapAdminEmails").Get<string[]>() ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .ToList();
    }
}
