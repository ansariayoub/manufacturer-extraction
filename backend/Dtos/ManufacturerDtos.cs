namespace ManufacturerExtraction.Api.Dtos;

public record ManufacturerDto(Guid Id, string Name, string? DefaultInstructions, DateTime CreatedDate);

public record CreateManufacturerRequest(string Name, string? DefaultInstructions);

public record UpdateManufacturerRequest(string? Name, string? DefaultInstructions);

public record ManufacturerPromptHistoryDto(Guid Id, string Instructions, DateTime CreatedDate);
