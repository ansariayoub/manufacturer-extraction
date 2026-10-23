using Microsoft.EntityFrameworkCore;
using ManufacturerExtraction.Api.Data;
using ManufacturerExtraction.Api.Models;

namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// The default system prompt the canonical-mapping pipeline sends to Azure OpenAI, editable from the
/// Settings page instead of being a constant baked into the code. The text compiled into
/// AnalyticsTransformationService stays as the factory default: nothing is stored until an
/// administrator saves a change, and "reset" simply deletes the stored row so the compiled default
/// applies again.
///
/// Same shape as <see cref="AiModelSettingsService"/> (singleton, one row read at startup, updated in
/// place on every save) because AnalyticsTransformationService is constructed once per document and
/// the value changes rarely. The JSON output schema is deliberately NOT editable here: its field
/// names are bound to the stored model, the viewer and the exports.
/// </summary>
public class ExtractionPromptSettingsService
{
    private const string SettingKey = "Prompt.BaseSystem";

    /// <summary>Generous ceiling that still protects the model's context window from a pasted novel.</summary>
    public const int MaxLength = 60_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private volatile string? _custom;

    public ExtractionPromptSettingsService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        LoadFromDatabase();
    }

    public string DefaultPrompt => AnalyticsTransformationService.DefaultBaseSystemPrompt;

    public bool IsCustomized => _custom is not null;

    /// <summary>What the next document processed will use.</summary>
    public string CurrentPrompt => _custom ?? DefaultPrompt;

    private void LoadFromDatabase()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var saved = db.AppSettings.AsNoTracking().FirstOrDefault(s => s.Key == SettingKey)?.Value;
            if (!string.IsNullOrWhiteSpace(saved)) _custom = saved;
        }
        catch
        {
            // The database may not be reachable yet at startup (serverless Azure SQL waking up) —
            // run on the compiled default rather than crash app startup over a preference.
        }
    }

    public async Task SetPromptAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("The prompt cannot be empty.", nameof(prompt));
        if (prompt.Length > MaxLength)
            throw new ArgumentException($"The prompt is limited to {MaxLength:N0} characters.", nameof(prompt));

        // Saving the factory text unchanged is the same as "not customised": store nothing, so a later
        // improvement to the compiled default still reaches this installation.
        if (string.Equals(Normalize(prompt), Normalize(DefaultPrompt), StringComparison.Ordinal))
        {
            await ResetAsync(ct);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.AppSettings.FindAsync(new object?[] { SettingKey }, ct);
        if (existing is null)
            db.AppSettings.Add(new AppSetting { Key = SettingKey, Value = prompt });
        else
            existing.Value = prompt;

        await db.SaveChangesAsync(ct);
        _custom = prompt;
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.AppSettings.FindAsync(new object?[] { SettingKey }, ct);
        if (existing is not null)
        {
            db.AppSettings.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
        _custom = null;
    }

    // Browsers send CRLF from a textarea while the compiled raw string uses LF.
    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}
