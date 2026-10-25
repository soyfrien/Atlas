using Dapper;
using Microsoft.Data.Sqlite;

namespace Atlas.Web.Services;

public sealed class PublicQuery
{
    public string? Q { get; set; }
    public string? Tag { get; set; }
    public string? Verification { get; set; }
    public string? Kind { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public int Limit { get; set; } = 24;
    public int Offset { get; set; }
}

public sealed class AdminQuery
{
    public string? Q { get; set; }
    public string? Status { get; set; }
    public int Limit { get; set; } = 50;
    public int Offset { get; set; }
}

public sealed class DocStore
{
    private const string SnippetOpen = "char(1)", SnippetClose = "char(2)";
    private readonly Database _db;

    public DocStore(Database db) => _db = db;

    private const string Cols = """
        d.id, d.title, d.description, d.doc_date, d.kind, d.ext, d.mime, d.original_name, d.size_bytes, d.sha256, d.status,
        d.verification, d.source_notes, d.created_at, d.published_at, d.updated_at, d.public_size, d.public_sha256,
        d.public_ext, d.page_count, d.duration_sec, d.redactions, d.error, d.has_thumb_original, d.has_thumb_public
        """;

    public static string Now() => DateTimeOffset.UtcNow.ToString("o");

    public void Insert(Doc d)
    {
        using var c = _db.Open();
        c.Execute("""
            INSERT INTO documents (id,title,description,doc_date,kind,ext,mime,original_name,size_bytes,sha256,status,verification,source_notes,
              created_at,updated_at,redactions)
            VALUES (@Id,@Title,@Description,@DocDate,@Kind,@Ext,@Mime,@OriginalName,@SizeBytes,@Sha256,@Status,@Verification,@SourceNotes,
              @CreatedAt,@UpdatedAt,@Redactions)
            """, d);
        c.Execute("INSERT OR IGNORE INTO doc_text(doc_id) VALUES (@Id)", new { d.Id });
        Reindex(c, d.Id);
    }

    public Doc? Get(string id)
    {
        using var c = _db.Open();
        return c.QuerySingleOrDefault<Doc>($"SELECT {Cols} FROM documents d WHERE d.id=@id", new { id });
    }

    public Doc? FindBySha(string sha)
    {
        using var c = _db.Open();
        return c.QueryFirstOrDefault<Doc>($"SELECT {Cols} FROM documents d WHERE d.sha256=@sha ORDER BY created_at LIMIT 1", new { sha });
    }

    public List<string> TagsOf(string id)
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT tag FROM tags WHERE doc_id=@id ORDER BY tag", new { id }).ToList();
    }

    public List<string> IdsWithStatus(params string[] statuses)
    {
        using var c = _db.Open();
        return c.Query<string>("SELECT id FROM documents WHERE status IN @s ORDER BY created_at", new { s = statuses }).ToList();
    }

    /// <summary>Saves the editable metadata and refreshes both search indexes.</summary>
    public void UpdateMetadata(string id, string title, string description, string? date, string verification, string sourceNotes,
        IReadOnlyList<string> tags, string redactionsJson)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("""
            UPDATE documents SET title=@title, description=@description, doc_date=@date, verification=@verification,
              source_notes=@sourceNotes, redactions=@redactionsJson, updated_at=@now WHERE id=@id
            """, new { id, title, description, date, verification, sourceNotes, redactionsJson, now = Now() }, tx);
        c.Execute("DELETE FROM tags WHERE doc_id=@id", new { id }, tx);
        foreach (var t in tags) c.Execute("INSERT INTO tags(doc_id, tag) VALUES (@id,@t)", new { id, t }, tx);
        Reindex(c, id, tx);
        tx.Commit();
    }

    public void SetStatus(string id, string status, string? error = null)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("UPDATE documents SET status=@status, error=@error, updated_at=@now WHERE id=@id",
            new { id, status, error, now = Now() }, tx);
        Reindex(c, id, tx);
        tx.Commit();
    }

    public sealed record IngestData(string Sha256, string TextOriginal, bool HasThumb, int? PageCount, double? DurationSec);

    public void SaveIngest(string id, IngestData r)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("""
            UPDATE documents SET sha256=@Sha256, has_thumb_original=@HasThumb, page_count=@PageCount, duration_sec=@DurationSec,
              status='pending', error=NULL, updated_at=@now WHERE id=@id
            """, new { id, r.Sha256, HasThumb = r.HasThumb ? 1 : 0, r.PageCount, r.DurationSec, now = Now() }, tx);
        c.Execute("INSERT INTO doc_text(doc_id, original) VALUES (@id,@t) ON CONFLICT(doc_id) DO UPDATE SET original=@t",
            new { id, t = r.TextOriginal }, tx);
        Reindex(c, id, tx);
        tx.Commit();
    }

    public sealed record PublishData(long Size, string Sha256, string Ext, string Mime, string TextPublic, bool HasThumb, int? PageCount);

    public void SavePublish(string id, PublishData r)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("""
            UPDATE documents SET status='published', published_at=@now, updated_at=@now, public_size=@Size, public_sha256=@Sha256,
              public_ext=@Ext, has_thumb_public=@HasThumb, error=NULL, page_count=COALESCE(@PageCount, page_count) WHERE id=@id
            """, new { id, r.Size, r.Sha256, r.Ext, HasThumb = r.HasThumb ? 1 : 0, r.PageCount, now = Now() }, tx);
        c.Execute("INSERT INTO doc_text(doc_id, public) VALUES (@id,@t) ON CONFLICT(doc_id) DO UPDATE SET public=@t",
            new { id, t = r.TextPublic }, tx);
        Reindex(c, id, tx);
        tx.Commit();
    }

    /// <summary>Clears everything that was derived for the public (public copy hash, public text) when an item leaves publication.</summary>
    public void ClearPublic(string id, string newStatus)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("""
            UPDATE documents SET status=@newStatus, published_at=NULL, public_size=NULL, public_sha256=NULL, public_ext=NULL,
              has_thumb_public=0, updated_at=@now WHERE id=@id
            """, new { id, newStatus, now = Now() }, tx);
        c.Execute("UPDATE doc_text SET public='' WHERE doc_id=@id", new { id }, tx);
        Reindex(c, id, tx);
        tx.Commit();
    }

    public void Delete(string id)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM fts_public WHERE id=@id", new { id }, tx);
        c.Execute("DELETE FROM fts_admin WHERE id=@id", new { id }, tx);
        c.Execute("DELETE FROM documents WHERE id=@id", new { id }, tx);
        c.Execute("DELETE FROM reports WHERE doc_id=@id", new { id }, tx);
        tx.Commit();
    }

    /// <summary>
    /// Rebuilds both search indexes for one item. The public index only ever holds published items and only the text of the
    /// published (redacted, sanitized) copy; the admin index holds everything, including the original's text.
    /// </summary>
    private static void Reindex(SqliteConnection c, string id, SqliteTransaction? tx = null)
    {
        c.Execute("DELETE FROM fts_public WHERE id=@id; DELETE FROM fts_admin WHERE id=@id;", new { id }, tx);
        var row = c.QuerySingleOrDefault<(string Title, string Description, string Status, string Tags, string Original, string Public)>("""
            SELECT d.title, d.description, d.status,
              COALESCE((SELECT group_concat(tag, ' ') FROM tags WHERE doc_id=d.id), ''),
              COALESCE(t.original, ''), COALESCE(t.public, '')
            FROM documents d LEFT JOIN doc_text t ON t.doc_id=d.id WHERE d.id=@id
            """, new { id }, tx);
        if (row.Title is null) return;
        c.Execute("INSERT INTO fts_admin(id,title,description,tags,body) VALUES (@id,@Title,@Description,@Tags,@Original)",
            new { id, row.Title, row.Description, row.Tags, row.Original }, tx);
        if (row.Status == Status.Published)
            c.Execute("INSERT INTO fts_public(id,title,description,tags,body) VALUES (@id,@Title,@Description,@Tags,@Public)",
                new { id, row.Title, row.Description, row.Tags, row.Public }, tx);
    }

    // ---------- reading ----------

    private sealed class Row : Doc { public string? Snip { get; set; } }

    private static void AddPublicFilters(PublicQuery q, List<string> where, DynamicParameters p)
    {
        if (!string.IsNullOrWhiteSpace(q.Tag)) { where.Add("EXISTS (SELECT 1 FROM tags t WHERE t.doc_id=d.id AND t.tag=@tag)"); p.Add("tag", q.Tag.Trim().ToLowerInvariant()); }
        if (Verification.IsValid(q.Verification)) { where.Add("d.verification=@ver"); p.Add("ver", q.Verification); }
        if (!string.IsNullOrWhiteSpace(q.Kind)) { where.Add("d.kind=@kind"); p.Add("kind", q.Kind.Trim().ToLowerInvariant()); }
        if (IsDate(q.From)) { where.Add("d.doc_date >= @from"); p.Add("from", q.From); }
        if (IsDate(q.To)) { where.Add("d.doc_date <= @to"); p.Add("to", q.To); }
    }

    public static bool IsDate(string? s) => s is { Length: 10 } && DateOnly.TryParseExact(s, "yyyy-MM-dd", out _);

    public PagedResult<PublicDoc> SearchPublic(PublicQuery q)
    {
        var limit = Math.Clamp(q.Limit, 1, 60);
        var offset = Math.Max(0, q.Offset);
        var match = FtsQuery.Build(q.Q);
        var where = new List<string> { "d.status='published'" };
        var p = new DynamicParameters();
        AddPublicFilters(q, where, p);
        p.Add("limit", limit); p.Add("offset", offset);
        string from, order, snip;
        if (match is not null)
        {
            where.Insert(0, "fts_public MATCH @m"); p.Add("m", match);
            from = "fts_public JOIN documents d ON d.id=fts_public.id";
            order = "bm25(fts_public, 0, 10, 4, 6, 1)";
            snip = $"snippet(fts_public, -1, {SnippetOpen}, {SnippetClose}, '…', 24)";
        }
        else
        {
            from = "documents d";
            order = "COALESCE(d.doc_date,'') DESC, d.published_at DESC";
            snip = "NULL";
        }
        var whereSql = string.Join(" AND ", where);
        using var c = _db.Open();
        var total = c.ExecuteScalar<int>($"SELECT count(*) FROM {from} WHERE {whereSql}", p);
        var rows = c.Query<Row>($"SELECT {Cols}, {snip} AS snip FROM {from} WHERE {whereSql} ORDER BY {order} LIMIT @limit OFFSET @offset", p).ToList();
        var tags = TagsFor(c, rows.Select(r => r.Id));
        return new PagedResult<PublicDoc>(rows.Select(r => ToPublic(r, tags.GetValueOrDefault(r.Id) ?? [], r.Snip)).ToList(), total, offset, limit);
    }

    public PublicDoc? GetPublic(string id)
    {
        using var c = _db.Open();
        var d = c.QuerySingleOrDefault<Doc>($"SELECT {Cols} FROM documents d WHERE d.id=@id AND d.status='published'", new { id });
        return d is null ? null : ToPublic(d, TagsFor(c, [id]).GetValueOrDefault(id) ?? [], null);
    }

    public PagedResult<AdminDoc> SearchAdmin(AdminQuery q)
    {
        var limit = Math.Clamp(q.Limit, 1, 200);
        var offset = Math.Max(0, q.Offset);
        var match = FtsQuery.Build(q.Q);
        var where = new List<string>();
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(q.Status) && q.Status != "all") { where.Add("d.status=@status"); p.Add("status", q.Status); }
        p.Add("limit", limit); p.Add("offset", offset);
        string from, order, snip;
        if (match is not null)
        {
            where.Insert(0, "fts_admin MATCH @m"); p.Add("m", match);
            from = "fts_admin JOIN documents d ON d.id=fts_admin.id";
            order = "bm25(fts_admin, 0, 10, 4, 6, 1)";
            snip = $"snippet(fts_admin, -1, {SnippetOpen}, {SnippetClose}, '…', 24)";
        }
        else { from = "documents d"; order = "d.created_at DESC"; snip = "NULL"; }
        var whereSql = where.Count == 0 ? "1=1" : string.Join(" AND ", where);
        using var c = _db.Open();
        var total = c.ExecuteScalar<int>($"SELECT count(*) FROM {from} WHERE {whereSql}", p);
        var rows = c.Query<Row>($"SELECT {Cols}, {snip} AS snip FROM {from} WHERE {whereSql} ORDER BY {order} LIMIT @limit OFFSET @offset", p).ToList();
        var tags = TagsFor(c, rows.Select(r => r.Id));
        return new PagedResult<AdminDoc>(rows.Select(r => ToAdmin(r, tags.GetValueOrDefault(r.Id) ?? [], r.Snip)).ToList(), total, offset, limit);
    }

    public AdminDoc? GetAdmin(string id)
    {
        var d = Get(id);
        return d is null ? null : ToAdmin(d, TagsOf(id), null);
    }

    public List<(string Tag, int Count)> PublicTags()
    {
        using var c = _db.Open();
        return c.Query<(string, int)>("""
            SELECT t.tag, count(*) FROM tags t JOIN documents d ON d.id=t.doc_id WHERE d.status='published'
            GROUP BY t.tag ORDER BY count(*) DESC, t.tag LIMIT 200
            """).ToList();
    }

    public Dictionary<string, int> CountsByStatus()
    {
        using var c = _db.Open();
        return c.Query<(string, int)>("SELECT status, count(*) FROM documents GROUP BY status").ToDictionary(x => x.Item1, x => x.Item2);
    }

    public List<(string Id, string? PublishedAt)> PublishedIds()
    {
        using var c = _db.Open();
        return c.Query<(string, string?)>("SELECT id, published_at FROM documents WHERE status='published' ORDER BY published_at DESC").ToList();
    }

    private static Dictionary<string, List<string>> TagsFor(SqliteConnection c, IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return [];
        return c.Query<(string DocId, string Tag)>("SELECT doc_id, tag FROM tags WHERE doc_id IN @list ORDER BY tag", new { list })
            .GroupBy(x => x.DocId).ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());
    }

    public static PublicDoc ToPublic(Doc d, List<string> tags, string? snippet) => new(
        d.Id, d.Title, d.Description, tags, d.DocDate, d.Kind, d.Verification, d.PublicSize, d.PublicSha256, d.PublishedAt,
        d.PageCount, d.DurationSec, FileKinds.MimeFor(d.PublicExt ?? d.Ext), d.PublicExt ?? d.Ext, d.HasThumbPublic, snippet);

    public static AdminDoc ToAdmin(Doc d, List<string> tags, string? snippet) => new(
        d.Id, d.Title, d.Description, tags, d.DocDate, d.Kind, d.Ext, d.Mime, d.OriginalName, d.SizeBytes, d.Sha256, d.Status,
        d.Verification, d.SourceNotes, d.CreatedAt, d.PublishedAt, d.UpdatedAt, d.PublicSize, d.PublicSha256, d.PublicExt,
        d.PageCount, d.DurationSec, Redaction.Parse(d.Redactions), d.Error, d.HasThumbOriginal, d.HasThumbPublic,
        d.Kind is Kinds.Pdf or Kinds.Image, snippet);

    // ---------- reports & settings ----------

    public long AddReport(string docId, string reason, string? contact, string message)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("""
            INSERT INTO reports(doc_id, reason, contact, message, created_at) VALUES (@docId,@reason,@contact,@message,@now);
            SELECT last_insert_rowid();
            """, new { docId, reason, contact, message, now = Now() });
    }

    public List<Report> Reports(string? status)
    {
        using var c = _db.Open();
        return c.Query<Report>("""
            SELECT r.id, r.doc_id, COALESCE(d.title, '(deleted)') AS doc_title, r.reason, r.contact, r.message, r.created_at, r.status
            FROM reports r LEFT JOIN documents d ON d.id=r.doc_id
            WHERE (@status IS NULL OR r.status=@status) ORDER BY r.id DESC LIMIT 500
            """, new { status }).ToList();
    }

    public bool SetReportStatus(long id, string status)
    {
        using var c = _db.Open();
        return c.Execute("UPDATE reports SET status=@status WHERE id=@id", new { id, status }) > 0;
    }

    public bool DeleteReport(long id)
    {
        using var c = _db.Open();
        return c.Execute("DELETE FROM reports WHERE id=@id", new { id }) > 0;
    }

    public int OpenReportCount()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("SELECT count(*) FROM reports WHERE status='open'");
    }

    public string? GetSetting(string key)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key=@key", new { key });
    }

    public void SetSetting(string key, string value)
    {
        using var c = _db.Open();
        c.Execute("INSERT INTO settings(key,value) VALUES (@key,@value) ON CONFLICT(key) DO UPDATE SET value=@value", new { key, value });
    }
}
