using System.Collections.Concurrent;
using System.Threading.Channels;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Services;

/// <summary>
/// Unbounded FIFO of document ids waiting to be processed.
///
/// Before this existed, POST /{id}/analyze started a Task.Run per document immediately. Dropping
/// a batch of 40 files therefore launched 40 pipelines at once, each of which itself fans out to
/// several concurrent Azure OpenAI calls — well over a hundred in flight. Azure answers that with
/// 429s and timeouts, which surfaced as failed or silently-partial documents, and the parallel
/// SQL writes made the (already slow) queue listing worse.
/// </summary>
public class DocumentProcessingQueue : IDocumentProcessingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    // Tracks documents that are already queued or actively being processed, so a duplicate
    // /analyze or /reanalyze call (double-click, or a retry racing the original request) can't
    // hand the same document id to two workers at once. Without this, both workers ran the full
    // pipeline in parallel and the second one's INSERT into AnalyticsExtractions hit the unique
    // index on DocumentId, failing the document outright — observed in production once worker
    // count went up, which made the race far more likely to actually land.
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public void Enqueue(Guid documentId)
    {
        if (_inFlight.TryAdd(documentId, 0))
            _channel.Writer.TryWrite(documentId);
    }

    public void MarkFinished(Guid documentId) => _inFlight.TryRemove(documentId, out _);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Drains the queue with a fixed number of workers, so at most MaxConcurrentDocuments files are
/// ever being processed at the same time regardless of how many the user drops at once.
/// </summary>
public class DocumentProcessingWorker : BackgroundService
{
    // Raised 2 -> 4 -> 10 by request, to let large batches move through faster. Each document
    // itself caps at MaxConcurrentChunksPerDocument (3) calls, so worst case is now ~30 concurrent
    // Azure OpenAI calls instead of ~6 originally. AnalyticsTransformationService retries on 429
    // (MaxTransientRetries = 5), which covers occasional throttling, but if the gpt-5.2 deployment's
    // rate limit is lower than this can sustain, expect more retries/slowdowns under full load —
    // check the deployment's quota in Azure AI Foundry (Models + endpoints) if that happens.
    private const int MaxConcurrentDocuments = 10;

    private readonly IDocumentProcessingQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProcessingCancellationRegistry _cancellationRegistry;
    private readonly ILogger<DocumentProcessingWorker> _logger;

    public DocumentProcessingWorker(
        IDocumentProcessingQueue queue,
        IServiceScopeFactory scopeFactory,
        IProcessingCancellationRegistry cancellationRegistry,
        ILogger<DocumentProcessingWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _cancellationRegistry = cancellationRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable
            .Range(0, MaxConcurrentDocuments)
            .Select(i => RunWorkerAsync(i, stoppingToken));

        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(int workerIndex, CancellationToken stoppingToken)
    {
        await foreach (var documentId in _queue.ReadAllAsync(stoppingToken))
        {
            // Each document gets its own CTS so DELETE /{id} can cancel just that one.
            var cts = _cancellationRegistry.Register(documentId);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stoppingToken);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IDocumentProcessingService>();
                await processor.ProcessAsync(documentId, linked.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker {Worker} stopping, document {DocumentId} left queued", workerIndex, documentId);
                return;
            }
            catch (Exception ex)
            {
                // ProcessAsync already records failures on the document row; this is the last-resort
                // net that keeps one bad file from killing the worker for every file behind it.
                _logger.LogError(ex, "Unhandled error processing document {DocumentId}", documentId);
            }
            finally
            {
                _cancellationRegistry.Remove(documentId);
                _queue.MarkFinished(documentId);
            }
        }
    }
}
