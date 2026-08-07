namespace ManufacturerExtraction.Api.Services.Interfaces;

public interface ISpreadsheetExtractionService
{
    /// <summary>
    /// Reads a .xlsx/.xls workbook and returns a raw-extraction JSON document in the same shape as
    /// the Content Understanding response, so it can be stored and consumed identically.
    /// </summary>
    Task<string> ExtractAsync(Stream fileStream, string fileName, CancellationToken ct);
}
