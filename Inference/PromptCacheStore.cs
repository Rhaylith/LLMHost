using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMHostProject.Core;
using LLMHostProject.Models;

namespace LLMHostProject.Inference;

/// <summary>Prompt-cache options (settings.json → <c>promptCache</c>).</summary>
public class PromptCacheSettings
{
    /// <summary>Write the idle prompt caches to disk when a model is unloaded and bring them back when it is loaded again.</summary>
    public bool AutoSave { get; set; }
    /// <summary>Saved caches may use at most this much disk; the least recently written are deleted first.</summary>
    public int MaxDiskMb { get; set; } = 2048;
}

/// <summary>Describes a prompt cache saved on disk.</summary>
public record SavedCache(string Name, int Tokens, long Bytes, DateTime SavedAt, string ModelId, string KvTypeK, string KvTypeV);

/// <summary>
/// Saved prompt caches live in <c>~/.llmhost/cache/&lt;model-key&gt;/&lt;name&gt;.kv</c> (llama.cpp sequence state, which also
/// holds the tokens) with a small <c>&lt;name&gt;.json</c> describing what it belongs to.
/// </summary>
public static class PromptCacheStore
{
    public static string Root => Path.Combine(AppPaths.Root, "cache");

    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && !name.StartsWith('.');

    /// <summary>Folder for one model's caches (keyed by library id + size, so a replaced file does not reuse stale state).</summary>
    public static string DirFor(LocalModel model)
    {
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(model.Id.ToLowerInvariant() + "|" + model.SizeBytes)))[..12].ToLowerInvariant();
        return Path.Combine(Root, key);
    }

    public static string KvPath(LocalModel model, string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("Cache names may contain letters, digits, '.', '-' and '_' (max 64 characters).");
        return Path.Combine(DirFor(model), name + ".kv");
    }

    private static string MetaPath(string kv) => Path.ChangeExtension(kv, ".json");

    public static void WriteMeta(string kv, SavedCache meta) =>
        File.WriteAllText(MetaPath(kv), JsonSerializer.Serialize(meta, Json.Options));

    public static SavedCache? ReadMeta(string kv)
    {
        try { return JsonSerializer.Deserialize<SavedCache>(File.ReadAllText(MetaPath(kv)), Json.Options); }
        catch { return null; }
    }

    public static IReadOnlyList<SavedCache> List(LocalModel model)
    {
        var dir = DirFor(model);
        if (!Directory.Exists(dir)) return Array.Empty<SavedCache>();
        var list = new List<SavedCache>();
        foreach (var kv in Directory.EnumerateFiles(dir, "*.kv"))
            if (ReadMeta(kv) is { } m) list.Add(m with { Bytes = new FileInfo(kv).Length });
        return list.OrderByDescending(m => m.SavedAt).ToList();
    }

    public static bool Delete(LocalModel model, string name)
    {
        string kv = KvPath(model, name);
        bool had = File.Exists(kv);
        foreach (var f in new[] { kv, MetaPath(kv) }) try { File.Delete(f); } catch { }
        return had;
    }

    /// <summary>Deletes the oldest caches (across all models) until the total is within <paramref name="maxBytes"/>.</summary>
    public static int EnforceLimit(long maxBytes)
    {
        if (maxBytes <= 0 || !Directory.Exists(Root)) return 0;
        var files = Directory.EnumerateFiles(Root, "*.kv", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f)).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        int removed = 0;
        foreach (var f in files)
        {
            if (total <= maxBytes) break;
            total -= f.Length; removed++;
            foreach (var p in new[] { f.FullName, MetaPath(f.FullName) }) try { File.Delete(p); } catch { }
        }
        return removed;
    }
}
