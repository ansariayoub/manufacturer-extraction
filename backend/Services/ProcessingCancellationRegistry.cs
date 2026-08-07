using System.Collections.Concurrent;
using ManufacturerExtraction.Api.Services.Interfaces;

namespace ManufacturerExtraction.Api.Services;

public class ProcessingCancellationRegistry : IProcessingCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokens = new();

    public CancellationTokenSource Register(Guid documentId)
    {
        var cts = new CancellationTokenSource();
        _tokens[documentId] = cts;
        return cts;
    }

    public void Cancel(Guid documentId)
    {
        if (_tokens.TryGetValue(documentId, out var cts))
            cts.Cancel();
    }

    public void Remove(Guid documentId)
    {
        if (_tokens.TryRemove(documentId, out var cts))
            cts.Dispose();
    }
}