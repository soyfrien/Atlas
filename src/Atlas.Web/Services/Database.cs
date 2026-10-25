using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Atlas.Web.Services;

/// <summary>SQLite access (WAL mode). The schema is created on startup.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    static Database() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public Database(IOptions<AtlasOptions> options)
    {
        var dir = options.Value.ResolvedDataDir;
        Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dir, "atlas.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 15,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;");
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        c.Execute("""
            CREATE TABLE IF NOT EXISTS documents (
              id TEXT PRIMARY KEY,
              title TEXT NOT NULL,
              description TEXT NOT NULL DEFAULT '',
              doc_date TEXT,
              kind TEXT NOT NULL,
              ext TEXT NOT NULL,
              mime TEXT NOT NULL,
              original_name TEXT NOT NULL,
              size_bytes INTEGER NOT NULL,
              sha256 TEXT,
              status TEXT NOT NULL,
              verification TEXT NOT NULL DEFAULT 'unverified',
              source_notes TEXT NOT NULL DEFAULT '',
              created_at TEXT NOT NULL,
              published_at TEXT,
              updated_at TEXT NOT NULL,
              public_size INTEGER,
              public_sha256 TEXT,
              public_ext TEXT,
              page_count INTEGER,
              duration_sec REAL,
              redactions TEXT NOT NULL DEFAULT '[]',
              error TEXT,
              has_thumb_original INTEGER NOT NULL DEFAULT 0,
              has_thumb_public INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_documents_status ON documents(status);
            CREATE INDEX IF NOT EXISTS ix_documents_sha ON documents(sha256);
            CREATE TABLE IF NOT EXISTS doc_text (
              doc_id TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,
              original TEXT NOT NULL DEFAULT '',
              public TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS tags (
              doc_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
              tag TEXT NOT NULL,
              PRIMARY KEY (doc_id, tag)
            );
            CREATE INDEX IF NOT EXISTS ix_tags_tag ON tags(tag);
            CREATE TABLE IF NOT EXISTS reports (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              doc_id TEXT NOT NULL,
              reason TEXT NOT NULL,
              contact TEXT,
              message TEXT NOT NULL,
              created_at TEXT NOT NULL,
              status TEXT NOT NULL DEFAULT 'open'
            );
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS fts_public USING fts5(
              id UNINDEXED, title, description, tags, body, tokenize = 'porter unicode61 remove_diacritics 2');
            CREATE VIRTUAL TABLE IF NOT EXISTS fts_admin USING fts5(
              id UNINDEXED, title, description, tags, body, tokenize = 'porter unicode61 remove_diacritics 2');
            """);
    }
}
