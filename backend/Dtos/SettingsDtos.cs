namespace ManufacturerExtraction.Api.Dtos;

public record AiModelSettingsDto(string Current, IReadOnlyList<string> Available);

public record UpdateAiModelRequest(string Deployment);

public record EmbeddingModelSettingsDto(string Current, IReadOnlyList<string> Available);

public record UpdateEmbeddingModelRequest(string Deployment);

public record ExtractionPromptDto(string Prompt, string DefaultPrompt, bool IsCustomized, string Schema, int MaxLength);

public record UpdateExtractionPromptRequest(string Prompt);
