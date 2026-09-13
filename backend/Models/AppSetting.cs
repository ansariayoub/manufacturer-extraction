namespace ManufacturerExtraction.Api.Models;

/// <summary>
/// Generic key/value store for small pieces of app-wide configuration an operator can change from
/// the Settings page without a deploy — first use is the Azure OpenAI deployment name the mapping
/// pipeline calls (see AiModelSettingsService), but the shape is deliberately generic so a future
/// setting doesn't need its own table and migration.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
