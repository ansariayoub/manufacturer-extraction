namespace ManufacturerExtraction.Api.Models;

/// <summary>
/// A manufacturer the app knows about, managed from the admin Settings page instead of the
/// previously hardcoded frontend list — an operator can add a new one the moment a report from a
/// manufacturer not seen before shows up, with no code change or deploy required.
///
/// <see cref="DefaultInstructions"/> is the per-manufacturer "processing instructions" template an
/// operator configures once (e.g. Rheem's per-sheet money-column pins, EEMAX's sheet filter) — the
/// upload screen pre-fills the instructions box with it whenever this manufacturer is selected, and
/// the operator can still freely edit that box for one specific import without changing the saved
/// default.
/// </summary>
public class Manufacturer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? DefaultInstructions { get; set; }
    public DateTime CreatedDate { get; set; }
}
