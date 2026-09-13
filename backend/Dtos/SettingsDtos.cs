namespace ManufacturerExtraction.Api.Dtos;

public record AiModelSettingsDto(string Current, IReadOnlyList<string> Available);

public record UpdateAiModelRequest(string Deployment);

public record EmbeddingModelSettingsDto(string Current, IReadOnlyList<string> Available);

public record UpdateEmbeddingModelRequest(string Deployment);
