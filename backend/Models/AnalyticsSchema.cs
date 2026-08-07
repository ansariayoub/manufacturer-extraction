namespace ManufacturerExtraction.Api.Models;

public class AnalyticsReport
{
    /// <summary>Lines exactly as they appear in the source document.</summary>
    public List<AnalyticsTransaction> Sales { get; set; } = new();

    /// <summary>
    /// For cumulative (year-to-date) reports only: the period's own activity, derived by
    /// subtracting the previous month's report line by line. Null for ordinary monthly reports.
    /// Stored alongside Sales so a cumulative document keeps both truths.
    /// </summary>
    public List<AnalyticsTransaction>? MonthlySales { get; set; }
}

public class AnalyticsTransaction
{
    public string? SourceName { get; set; }
    public string? Manufacturer { get; set; }
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public DateOnly? Date { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ProductFamily { get; set; }
    public string? PartNo { get; set; }
    public string? PartDescription { get; set; }
    // decimal, not long: the JSON schema declares this as "number", and a model returning e.g.
    // 2.5 used to throw a JsonException that failed the whole chunk (and every row in it).
    public decimal? Quantity { get; set; }
    public double? NetSales { get; set; }
    public double? Commission { get; set; }
}