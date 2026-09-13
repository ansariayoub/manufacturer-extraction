using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// Which Azure OpenAI embedding deployment is selected in Settings — mirrors
/// <see cref="AiModelSettingsService"/> exactly (same singleton/in-memory-cache reasoning), kept as
/// its own small class rather than a shared generic one so each stays simple and obviously correct
/// on its own; the duplication here is a few lines, not a maintenance burden.
///
/// Nothing in the pipeline calls an embedding model yet — this exists so the choice ("text-
/// embedding-3-large" vs the newly deployed, cheaper "text-embedding-3-small") is already in place
/// and switchable from the UI the moment an embedding-based feature is built, the same way the AI
/// model deployment already is for the canonical mapper.
/// </summary>
public class EmbeddingModelSettingsService
{
    private const string SettingKey = "EmbeddingModel.DeploymentName";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _defaultDeployment;
    private volatile string _currentDeployment;

    public IReadOnlyList<string> AvailableDeployments { get; }

    public EmbeddingModelSettingsService(IServiceScopeFactory scopeFactory, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _defaultDeployment = config["AzureOpenAI:EmbeddingDeploymentName"] ?? "text-embedding-3-large";
        _currentDeployment = _defaultDeployment;

        var configured = config.GetSection("AzureOpenAI:AvailableEmbeddingDeployments").Get<string[]>();
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
            // Database may not be reachable yet at startup — fall back to the configured default.
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
