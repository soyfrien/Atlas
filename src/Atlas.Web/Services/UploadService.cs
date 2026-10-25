using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Atlas.Web.Services;

public sealed record UploadInfo(string Id, string Name, long Size, long Received);

public sealed class UploadException(int status, string message, long? received = null) : Exception(message)
{
    public int Status { get; } = status;
    public long? Received { get; } = received;
}

/// <summary>
/// Resumable chunked uploads. Each chunk is streamed straight to a .part file on disk (never buffered in memory), so
/// multi-gigabyte videos work and an interrupted phone upload can continue from where the server stopped.
/// </summary>
public sealed class UploadService
{
    private sealed record Meta(string Name, long Size, string Created);

    private static readonly Regex IdRx = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private readonly AtlasOptions _o;
    private readonly DataPaths _paths;
    private readonly DocStore _store;
    private readonly WorkQueue _queue;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public UploadService(IOptions<AtlasOptions> o, DataPaths paths, DocStore store, WorkQueue queue)
    {
        _o = o.Value; _paths = paths; _store = store; _queue = queue;
    }

    private string Part(string id) => Path.Combine(_paths.Uploads, id + ".part");
    private string MetaPath(string id) => Path.Combine(_paths.Uploads, id + ".json");

    public UploadInfo Init(string? name, long size)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 255) throw new UploadException(400, "A file name is required.");
        var ext = FileKinds.ExtOf(name);
        if (!FileKinds.TryGet(ext, out _, out _))
            throw new UploadException(415, "That file type isn't supported. Supported: " + FileKinds.SupportedList + ".");
        if (size <= 0) throw new UploadException(400, "The file is empty.");
        if (size > _o.MaxUploadBytes) throw new UploadException(413, $"The file is larger than the limit of {_o.MaxUploadBytes / (1L << 20)} MB.");
        if (_paths.FreeBytes() - size < _o.MinFreeBytes) throw new UploadException(507, "Not enough free disk space for that file.");
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        File.WriteAllText(MetaPath(id), JsonSerializer.Serialize(new Meta(name, size, DocStore.Now())));
        using (File.Create(Part(id))) { }
        return new UploadInfo(id, name, size, 0);
    }

    private Meta ReadMeta(string id)
    {
        if (!IdRx.IsMatch(id) || !File.Exists(MetaPath(id))) throw new UploadException(404, "Unknown upload.");
        return JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath(id)))!;
    }

    public UploadInfo GetStatus(string id)
    {
        var m = ReadMeta(id);
        return new UploadInfo(id, m.Name, m.Size, new FileInfo(Part(id)).Length);
    }

    /// <summary>Appends the request body at <paramref name="offset"/>. The offset must equal the bytes already stored.</summary>
    public async Task<UploadInfo> AppendAsync(string id, long offset, Stream body, CancellationToken ct)
    {
        var m = ReadMeta(id);
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new UploadException(409, "Another chunk of this upload is still being written.");
        try
        {
            await using var fs = new FileStream(Part(id), FileMode.Open, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.Asynchronous);
            if (fs.Length != offset) throw new UploadException(409, "Offset does not match what the server has.", fs.Length);
            fs.Seek(0, SeekOrigin.End);
            var remaining = m.Size - offset;
            var buffer = new byte[1 << 20];
            long chunk = 0;
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                chunk += n;
                if (chunk > remaining || chunk > _o.MaxChunkBytes)
                {
                    fs.SetLength(offset);
                    throw new UploadException(413, "That chunk is larger than allowed.", offset);
                }
                await fs.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            await fs.FlushAsync(ct);
            return new UploadInfo(id, m.Name, m.Size, fs.Length);
        }
        finally { gate.Release(); }
    }

    /// <summary>Turns a finished upload into a pending-review item and queues processing.</summary>
    public Doc Complete(string id)
    {
        var m = ReadMeta(id);
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) throw new UploadException(409, "The upload is still being written.");
        try
        {
            var part = Part(id);
            var len = new FileInfo(part).Length;
            if (len != m.Size) throw new UploadException(409, "The upload is incomplete.", len);
            var ext = FileKinds.ExtOf(m.Name);
            FileKinds.TryGet(ext, out var kind, out var mime);
            var docId = DataPaths.NewId();
            var dir = _paths.ItemDir(docId);
            Directory.CreateDirectory(dir);
            File.Move(part, Path.Combine(dir, "original." + ext));
            File.Delete(MetaPath(id));
            var now = DocStore.Now();
            var doc = new Doc
            {
                Id = docId, Title = TitleFromName(m.Name), Kind = kind, Ext = ext, Mime = mime, OriginalName = m.Name,
                SizeBytes = len, Status = Status.Processing, CreatedAt = now, UpdatedAt = now,
            };
            _store.Insert(doc);
            _queue.Enqueue(JobKind.Ingest, docId);
            return doc;
        }
        finally { gate.Release(); _locks.TryRemove(id, out _); }
    }

    public void Abort(string id)
    {
        ReadMeta(id);
        File.Delete(Part(id)); File.Delete(MetaPath(id));
    }

    /// <summary>Deletes abandoned partial uploads older than <paramref name="maxAge"/>.</summary>
    public int CleanupStale(TimeSpan maxAge)
    {
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(_paths.Uploads))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > maxAge) { try { File.Delete(f); n++; } catch { /* ignore */ } }
        return n;
    }

    public static string TitleFromName(string name)
    {
        var t = Regex.Replace(Path.GetFileNameWithoutExtension(name), @"[_\-.]+", " ").Trim();
        t = Regex.Replace(t, @"\s+", " ");
        if (t.Length == 0) t = "Untitled";
        return t.Length > 200 ? t[..200] : t;
    }
}

/// <summary>Deletes abandoned partial uploads every few hours.</summary>
public sealed class UploadCleanup : BackgroundService
{
    private readonly UploadService _uploads;
    public UploadCleanup(UploadService uploads) => _uploads = uploads;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(3));
        do { try { _uploads.CleanupStale(TimeSpan.FromDays(3)); } catch { /* best effort */ } }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
