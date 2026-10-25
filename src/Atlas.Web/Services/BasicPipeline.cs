using System.Text;

namespace Atlas.Web.Services;

/// <summary>Minimal pipeline: hashes the file and copies it as-is. Replaced by ToolPipeline in the real app.</summary>
public sealed class BasicPipeline : IMediaPipeline
{
    private readonly DataPaths _paths;
    public BasicPipeline(DataPaths paths) => _paths = paths;

    public async Task<DocStore.IngestData> IngestAsync(Doc doc, CancellationToken ct)
    {
        var sha = await Hashing.Sha256FileAsync(_paths.Original(doc), ct);
        var text = doc.Kind == Kinds.Text ? await File.ReadAllTextAsync(_paths.Original(doc), Encoding.UTF8, ct) : "";
        return new DocStore.IngestData(sha, text, false, null, null);
    }

    public async Task<DocStore.PublishData> PublishAsync(Doc doc, CancellationToken ct)
    {
        var ext = FileKinds.PublicExt(doc.Ext);
        var dest = _paths.PublicFor(doc.Id, ext);
        File.Copy(_paths.Original(doc), dest, true);
        var info = new FileInfo(dest);
        var text = doc.Kind == Kinds.Text ? await File.ReadAllTextAsync(dest, Encoding.UTF8, ct) : "";
        return new DocStore.PublishData(info.Length, await Hashing.Sha256FileAsync(dest, ct), ext, FileKinds.MimeFor(ext), text, false, doc.PageCount);
    }
}
