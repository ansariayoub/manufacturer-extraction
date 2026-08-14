namespace ManufacturerExtraction.Api.Services.Interfaces;

public interface ISpreadsheetExtractionService
{
    /// <summary>
    /// Reads a .xlsx/.xls workbook and returns a raw-extraction JSON document in the same shape as
    /// the Content Understanding response, so it can be stored and consumed identically.
    /// </summary>
    /// <param name="onlySheetName">
    /// When set, restricts extraction to the one sheet whose name matches (case-insensitively).
    /// Comes from a "only read the sheet named ..." style custom instruction — see
    /// <see cref="CustomInstructionsParser"/>. Other sheets are skipped entirely, not just filtered
    /// out downstream, so this also avoids reading/storing rows the user never wanted in the first
    /// place (the reason it exists: a 6-sheet, 75k-row workbook where only one sheet was relevant).
    /// </param>
    Task<string> ExtractAsync(Stream fileStream, string fileName, CancellationToken ct, string? onlySheetName = null);
}
