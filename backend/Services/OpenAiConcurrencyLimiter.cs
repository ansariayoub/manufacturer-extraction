namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// Caps how many Azure OpenAI chat completion calls are ever in flight at once, across ALL
/// documents being processed at the same time — not per document.
///
/// Before this existed, AnalyticsTransformationService created its own SemaphoreSlim(3) inside
/// TransformAsync, but that service is registered AddScoped, so every document got its own
/// semaphore. With DocumentProcessingWorker now running up to 10 documents in parallel, the
/// uncoordinated worst case was 10 documents x 3 chunks = up to 30 simultaneous calls against a
/// single Azure OpenAI deployment with a fixed token/request-per-minute quota. Once past that
/// quota the deployment throttles (429s) and the SDK's own transport retries stack delay on top
/// of our exponential backoff, which is what turned normal-sized files into 40+ minute runs and
/// caused chunks to exhaust their retries and drop rows — surfacing later as the "Incomplete
/// extraction" warning on documents whose totals genuinely lost data.
///
/// One shared limiter fixes both: throughput becomes predictable regardless of how many documents
/// happen to be queued at once, and every chunk gets a fair shot at the deployment's real capacity
/// instead of a hidden multiplier nobody could see.
/// </summary>
public class OpenAiConcurrencyLimiter
{
    // Tune this to the Azure OpenAI deployment's actual rate limit (Azure AI Foundry -> Models +
    // endpoints -> the gpt-5.2 deployment -> quota). If chunks still throttle heavily with this
    // value, lower it; if the deployment has real headroom to spare, it can go up.
    //
    // Checked against the real deployment: 15,000,000 TPM / 150,000 RPM. A chunk call (a ~90-row
    // table plus schema and system prompt) is nowhere near large enough to make 8 concurrent calls
    // the real ceiling here — that number was a conservative guess made before the quota was known,
    // and it was the actual bottleneck behind documents taking far longer than they needed to.
    // Raised 5x; there is still enormous headroom left at this value (40 concurrent calls is a
    // small fraction of either limit) to go higher later if throughput needs it.
    private const int MaxConcurrentCalls = 40;

    private readonly SemaphoreSlim _semaphore = new(MaxConcurrentCalls);

    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    public void Release() => _semaphore.Release();
}
