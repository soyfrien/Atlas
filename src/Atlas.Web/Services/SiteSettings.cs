namespace Atlas.Web.Services;

public sealed record SiteConfig(bool NoIndex, string Title, string Intro, string ContactNote);

/// <summary>Admin-editable site settings, cached in memory. Search engines are kept out (noindex) until the admin turns that off.</summary>
public sealed class SiteSettings
{
    private readonly DocStore _store;
    private volatile SiteConfig _current;

    public SiteSettings(DocStore store)
    {
        _store = store;
        _current = Load();
    }

    public SiteConfig Current => _current;

    private SiteConfig Load() => new(
        NoIndex: _store.GetSetting("noindex") != "false",
        Title: _store.GetSetting("title") ?? "Atlas",
        Intro: _store.GetSetting("intro") ?? "A public archive of documents, recordings and images. Every item is reviewed before it is published.",
        ContactNote: _store.GetSetting("contact_note") ?? "");

    public SiteConfig Save(bool noIndex, string title, string intro, string contactNote)
    {
        _store.SetSetting("noindex", noIndex ? "true" : "false");
        _store.SetSetting("title", title);
        _store.SetSetting("intro", intro);
        _store.SetSetting("contact_note", contactNote);
        return _current = Load();
    }
}
