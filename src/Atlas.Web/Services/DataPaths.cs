using Microsoft.Extensions.Options;

namespace Atlas.Web.Services;

/// <summary>Where things live on disk. Originals are private; only files under "public" names are ever served to visitors.</summary>
public sealed class DataPaths
{
    public string Root { get; }
    public string Uploads { get; }
    public string Keys { get; }

    public DataPaths(IOptions<AtlasOptions> options)
    {
        Root = options.Value.ResolvedDataDir;
        Uploads = Path.Combine(Root, "uploads");
        Keys = Path.Combine(Root, "keys");
        Directory.CreateDirectory(Uploads);
        Directory.CreateDirectory(Keys);
        Directory.CreateDirectory(Path.Combine(Root, "items"));
    }

    public string ItemDir(string id)
    {
        if (!IsSafeId(id)) throw new ArgumentException("bad id");
        return Path.Combine(Root, "items", id);
    }

    public string Original(Doc d) => Path.Combine(ItemDir(d.Id), "original." + d.Ext);
    public string Public(Doc d) => Path.Combine(ItemDir(d.Id), "public." + (d.PublicExt ?? d.Ext));
    public string PublicFor(string id, string ext) => Path.Combine(ItemDir(id), "public." + ext);
    public string ThumbOriginal(string id) => Path.Combine(ItemDir(id), "thumb-original.jpg");
    public string ThumbPublic(string id) => Path.Combine(ItemDir(id), "thumb-public.jpg");
    public string PagesDir(string id, bool publicCopy) => Path.Combine(ItemDir(id), publicCopy ? "pages-public" : "pages-original");

    public static bool IsSafeId(string? id) => id is { Length: >= 6 and <= 40 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    public static string NewId()
    {
        const string alphabet = "abcdefghjkmnpqrstvwxyz23456789";
        Span<byte> b = stackalloc byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return string.Create(12, b.ToArray(), (span, bytes) => { for (var i = 0; i < span.Length; i++) span[i] = alphabet[bytes[i] % alphabet.Length]; });
    }

    /// <summary>Removes the public copy and everything derived from it (public thumbnail, cached page images).</summary>
    public void DeletePublicArtifacts(Doc d)
    {
        var dir = ItemDir(d.Id);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "public.*")) File.Delete(f);
        if (File.Exists(ThumbPublic(d.Id))) File.Delete(ThumbPublic(d.Id));
        var pages = PagesDir(d.Id, true);
        if (Directory.Exists(pages)) Directory.Delete(pages, true);
    }

    public void DeleteItem(string id)
    {
        var dir = ItemDir(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    public long FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace; } catch { return long.MaxValue; }
    }
}
