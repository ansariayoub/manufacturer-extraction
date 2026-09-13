using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// Which Azure OpenAI deployment the canonical-mapping pipeline calls, changeable from the
/// Settings page instead of being fixed at deploy time in appsettings.json/App Service config.
///
/// A newer deployment can turn out cheaper or faster on the same mapping task (the immediate case:
/// "gpt-5.6-luna" was added alongside "gpt-5.2" because it appears to use noticeably fewer tokens
/// for the same job) without anyone needing code access to try it — an operator flips it in
/// Settings, the very next document processed picks it up.
///
/// Registered as a singleton and cached in memory (one row read at startup, updated in place on
/// every SetDeploymentAsync) rather than read fresh from the database on every document processed:
/// AnalyticsTransformationService is constructed once per document, so under a large batch this
/// would otherwise be one extra database round trip per file for a value that changes maybe a
/// handful of times a year.
/// </summary>
public class AiModelSettingsService
{
    private const string SettingKey = "AiModel.DeploymentName";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _defaultDeployment;
    private volatile string _currentDeployment;

    public IReadOnlyList<string> AvailableDeployments { get; }

    public AiModelSettingsService(IServiceScopeFactory scopeFactory, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _defaultDeployment = config["AzureOpenAI:DeploymentName"]
            ?? throw new InvalidOperationException("Azure OpenAI deployment name missing");
        _currentDeployment = _defaultDeployment;

        var configured = config.GetSection("AzureOpenAI:AvailableDeployments").Get<string[]>();
        // The configured default is always offered even if someone forgot to list it explicitly —
        // an operator should never be unable to select the deployment the app is already running.
        AvailableDeployments = (configured is { Length: > 0 } ? configured : new[] { _defaultDeployment })
            .Union(new[] { _defaultDeployment }, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LoadFromDatabase();
    }

    public string CurrentDeployment => _currentDeployment;

    private void LoadFromDatabase()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var saved = db.AppSettings.AsNoTracking().FirstOrDefault(s => s.Key == SettingKey)?.Value;
            if (!string.IsNullOrWhiteSpace(saved)) _currentDeployment = saved;
        }
        catch
        {
            // The database may not be reachable yet at startup (serverless Azure SQL waking up) —
            // fall back to the configured default rather than crash app startup over a preference.
        }
    }

    public async Task SetDeploymentAsync(string deployment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deployment))
            throw new ArgumentException("Deployment name is required.", nameof(deployment));

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.AppSettings.FindAsync(new object?[] { SettingKey }, ct);
        if (existing is null)
            db.AppSettings.Add(new AppSetting { Key = SettingKey, Value = deployment });
        else
            existing.Value = deployment;

        await db.SaveChangesAsync(ct);
        _currentDeployment = deployment;
    }
}
