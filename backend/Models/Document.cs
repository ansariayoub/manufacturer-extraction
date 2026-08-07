namespace ManufacturerExtraction.Api.Models;

public enum ProcessingStatus
{
    Queued,
    Extracting,
    Mapping,
    Done,
    Failed
}

public class Document
{
    public Guid Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string BlobUrl { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime UploadDate { get; set; }
    public ProcessingStatus ProcessingStatus { get; set; }
    public double ProgressPct { get; set; }
    public string? ContentUnderstandingJobId { get; set; }
    public DateTime? AnalysisCompletedDate { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// True when the document finished but the pipeline knows it lost or could not verify part of
    /// the data (a truncated chunk, a chunk that failed every retry, a large row-count shortfall).
    /// ErrorMessage then carries the details. The point is that a document in this state must NOT
    /// be read as a trustworthy total — previously these cases were marked plainly "Done".
    /// </summary>
    public bool HasWarnings { get; set; }

    public decimal? TotalNetSales { get; set; }
    public decimal? TotalCommission { get; set; }
    public int? LineCount { get; set; }
    public int? CustomerCount { get; set; }

    /// <summary>
    /// True when the report accumulates from the start of the year (a "YTD" report) rather than
    /// covering only its own month. Comparing such a total against a monthly dashboard figure is
    /// meaningless, which is why it is flagged explicitly.
    /// </summary>
    /// <remarks>
    /// The month's own figure is deliberately NOT stored: it is derived at read time from this
    /// document's total and the previous period's, so that no file's processing depends on another
    /// and the value appears regardless of the order the reports were uploaded in.
    /// </remarks>
    public bool IsCumulative { get; set; }
    public string Manufacturer { get; set; } = string.Empty;
    public string PeriodMonth { get; set; } = string.Empty;
    public string PeriodYear { get; set; } = string.Empty;
    public string? CustomInstructions { get; set; }

    public RawExtraction? RawExtraction { get; set; }
    public AnalyticsExtraction? AnalyticsExtraction { get; set; }

    /// <summary>
    /// Computes and stores the four aggregate columns the queue listing reads. Single definition
    /// on purpose: the listing DTO used to recompute these itself from the JSON with a slightly
    /// different customer-count rule, so the two never quite agreed.
    /// </summary>
    public void ApplyAggregates(IReadOnlyCollection<AnalyticsTransaction> sales)
    {
        TotalNetSales = sales.Sum(s => (decimal?)s.NetSales ?? 0m);
        TotalCommission = sales.Sum(s => (decimal?)s.Commission ?? 0m);
        LineCount = sales.Count;
        CustomerCount = sales
            .Select(s => s.CustomerId ?? s.CustomerName)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct()
            .Count();
    }
}