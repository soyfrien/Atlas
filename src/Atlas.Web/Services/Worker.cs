using System.Threading.Channels;

namespace Atlas.Web.Services;

public enum JobKind { Ingest, Publish }

public sealed class WorkQueue
{
    private readonly Channel<(JobKind Kind, string Id)> _channel = Channel.CreateUnbounded<(JobKind, string)>();
    public void Enqueue(JobKind kind, string id) => _channel.Writer.TryWrite((kind, id));
    public IAsyncEnumerable<(JobKind Kind, string Id)> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Runs ingest/publish jobs one at a time (heavy tools on a small VM). Picks up unfinished work on startup.</summary>
public sealed class Worker : BackgroundService
{
    private readonly WorkQueue _queue;
    private readonly DocStore _store;
    private readonly IMediaPipeline _pipeline;
    private readonly DataPaths _paths;
    private readonly ILogger<Worker> _log;

    public Worker(WorkQueue queue, DocStore store, IMediaPipeline pipeline, DataPaths paths, ILogger<Worker> log)
    {
        _queue = queue; _store = store; _pipeline = pipeline; _paths = paths; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var id in _store.IdsWithStatus(Status.Processing)) _queue.Enqueue(JobKind.Ingest, id);
        foreach (var id in _store.IdsWithStatus(Status.Publishing)) _queue.Enqueue(JobKind.Publish, id);

        await foreach (var (kind, id) in _queue.ReadAllAsync(ct))
        {
            try
            {
                if (kind == JobKind.Ingest) await IngestAsync(id, ct);
                else await PublishAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Job {Kind} for {Id} failed", kind, id);
                var msg = ex is PipelineException ? ex.Message : "Processing failed (" + ex.GetType().Name + "). See the server log.";
                try { _store.SetStatus(id, kind == JobKind.Ingest ? Status.Failed : Status.Pending, msg); } catch { /* item may have been deleted */ }
            }
        }
    }

    private async Task IngestAsync(string id, CancellationToken ct)
    {
        var doc = _store.Get(id);
        if (doc is null || doc.Status != Status.Processing) return;
        var result = await _pipeline.IngestAsync(doc, ct);
        if (_store.Get(id) is null) return;
        _store.SaveIngest(id, result);
        if (_store.FindBySha(result.Sha256) is { } other && other.Id != id)
            _store.SetStatus(id, Status.Pending, $"Possible duplicate of item {other.Id} (same SHA-256).");
    }

    private async Task PublishAsync(string id, CancellationToken ct)
    {
        var doc = _store.Get(id);
        if (doc is null || doc.Status != Status.Publishing) return;
        var result = await _pipeline.PublishAsync(doc, ct);
        if (_store.Get(id) is not { Status: Status.Publishing }) { _paths.DeletePublicArtifacts(doc); return; }
        _store.SavePublish(id, result);
    }
}
