using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLMHostProject.Core;

namespace LLMHostProject.Models;

public record CatalogEntry(string Key, string Family, string Name, string Params, string Repo,
    string RecommendedQuant, string[] Tags, string Notes = "")
{
    /// <summary>True for models that accept images (multi-modal); everything else is a text LLM.</summary>
    public bool IsVision => Tags.Contains("vision");
}


/// <summary>
/// Curated list of major open-weight model families published as GGUF on Hugging Face ("staff picks"). The built-in list is the embedded
/// <c>catalog.json</c>; <c>settings.catalogUrl</c> can point at a newer copy, which is validated, cached under <c>cache/catalog.json</c> and
/// used from then on (ETag revalidation). A bad or unreachable remote list never replaces a working one.
/// </summary>
public static class Catalog
{
    private static readonly object Gate = new();
    private static IReadOnlyList<CatalogEntry>? _entries;
    private static string _updated = "";
    private static string _source = "built-in";

    public static IReadOnlyList<CatalogEntry> Entries { get { EnsureLoaded(); return _entries!; } }
    /// <summary>Date of the list (the <c>updated</c> field of the JSON).</summary>
    public static string Updated { get { EnsureLoaded(); return _updated; } }
    /// <summary>built-in | cached copy of the remote list.</summary>
    public static string Source { get { EnsureLoaded(); return _source; } }

    private static string CacheFile => Path.Combine(AppPaths.Root, "cache", "catalog.json");
    private static string EtagFile => Path.Combine(AppPaths.Root, "cache", "catalog.etag");

    private static void EnsureLoaded()
    {
        if (_entries != null) return;
        lock (Gate)
        {
            if (_entries != null) return;
            try
            {
                if (File.Exists(CacheFile))
                {
                    var (e, u) = Parse(File.ReadAllText(CacheFile));
                    (_entries, _updated, _source) = (e, u, "remote (cached)");
                    return;
                }
            }
            catch { /* fall back to the built-in list */ }
            (_entries, _updated) = Parse(BuiltInJson());
            _source = "built-in";
        }
    }

    /// <summary>Forget the loaded list (tests, and after the cache file changed).</summary>
    public static void Reset() { lock (Gate) { _entries = null; } }

    private static string BuiltInJson()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("catalog.json") ?? throw new InvalidOperationException("catalog.json is not embedded.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Checks the schema and returns the entries. Throws <see cref="FormatException"/> naming the first problem.</summary>
    public static (IReadOnlyList<CatalogEntry> Entries, string Updated) Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException ex) { throw new FormatException("The catalog is not valid JSON: " + ex.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new FormatException("The catalog needs an 'entries' array.");
            string updated = root.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "";
            var list = new List<CatalogEntry>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (var e in arr.EnumerateArray())
            {
                string Str(string name, bool required = true)
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString()!;
                    if (required) throw new FormatException($"Catalog entry {i}: '{name}' is missing.");
                    return "";
                }
                string key = Str("key"), repo = Str("repo");
                if (!HuggingFace.ValidRepo(repo)) throw new FormatException($"Catalog entry '{key}': '{repo}' is not a Hugging Face repository (owner/name).");
                if (!keys.Add(key)) throw new FormatException($"Catalog entry '{key}' appears twice.");
                var tags = e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToArray() : Array.Empty<string>();
                list.Add(new CatalogEntry(key, Str("family"), Str("name"), Str("params", false), repo, Str("recommendedQuant", false), tags, Str("notes", false)));
                i++;
            }
            if (list.Count == 0) throw new FormatException("The catalog has no entries.");
            return (list, updated);
        }
    }

    /// <summary>Downloads <paramref name="url"/> (conditional on the stored ETag), validates it and makes it the current list.</summary>
    /// <returns>true when a newer list was installed, false when the cached one is still current.</returns>
    public static async Task<bool> RefreshAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("The catalog URL must be an http(s) address.");
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        try { if (File.Exists(EtagFile) && File.Exists(CacheFile)) req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(File.ReadAllText(EtagFile).Trim())); } catch { }
        using var resp = await HuggingFace.Client.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotModified) return false;
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (body.Length > 2_000_000) throw new FormatException("The catalog is unreasonably large.");
        var (entries, updated) = Parse(body);                               // throws before anything is stored
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
        File.WriteAllText(CacheFile, body);
        if (resp.Headers.ETag != null) File.WriteAllText(EtagFile, resp.Headers.ETag.ToString()); else File.Delete(EtagFile);
        lock (Gate) { _entries = entries; _updated = updated; _source = "remote (cached)"; }
        return true;
    }

    /// <summary>Removes the cached remote list and goes back to the built-in one.</summary>
    public static void UseBuiltIn()
    {
        try { File.Delete(CacheFile); File.Delete(EtagFile); } catch { }
        Reset();
    }


    public static CatalogEntry? Find(string keyOrRepo) =>
        Entries.FirstOrDefault(e => e.Key.Equals(keyOrRepo, StringComparison.OrdinalIgnoreCase)
                                    || e.Repo.Equals(keyOrRepo, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pick the file that best matches a quantization name (case-insensitive), else the closest to Q4_K_M.</summary>
    public static RemoteFile? PickFile(IReadOnlyList<RemoteFile> files, string? quant)
    {
        if (files.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(quant))
        {
            var exact = files.FirstOrDefault(f => f.Quant.Equals(quant, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        foreach (var q in new[] { "Q4_K_M", "Q4_K_S", "Q5_K_M", "Q4_0", "Q8_0" })
        {
            var f = files.FirstOrDefault(x => x.Quant == q);
            if (f != null) return f;
        }
        return files[0];
    }
}
