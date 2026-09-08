namespace ManufacturerExtraction.Api.Models;

/// <summary>
/// A past value of a manufacturer's <see cref="Manufacturer.DefaultInstructions"/>, kept whenever
/// it changes so an operator can see what was used before (a report layout can require reverting
/// to a previous prompt, e.g. a per-sheet money-column pin that changed and changed back) and
/// restore it without retyping. Snapshots the OLD value at the moment of a change, not the new
/// one — the manufacturer row itself is always the current value, so history is only ever the
/// trail of what it used to be.
/// </summary>
public class ManufacturerPromptHistory
{
    public Guid Id { get; set; }
    public Guid ManufacturerId { get; set; }
    public string Instructions { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }

    public Manufacturer? Manufacturer { get; set; }
}
