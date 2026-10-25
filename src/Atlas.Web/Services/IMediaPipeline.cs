namespace Atlas.Web.Services;

/// <summary>
/// The heavy lifting for an item. Ingest runs right after upload (hash, text extraction, thumbnail); Publish builds the
/// sanitized, redacted public copy. Implementations must fail closed: if a public copy can't be made safely, throw.
/// </summary>
public interface IMediaPipeline
{
    Task<DocStore.IngestData> IngestAsync(Doc doc, CancellationToken ct);
    Task<DocStore.PublishData> PublishAsync(Doc doc, CancellationToken ct);
}

/// <summary>Raised for problems the admin can act on; the message is shown in the admin UI (never include paths or secrets).</summary>
public sealed class PipelineException(string message) : Exception(message);
