using System.Text.Json;

namespace LLMHostProject.Core;

/// <summary>Load settings and inference settings remembered for one model.</summary>
public class ModelConfig
{
    /// <summary>Applied when the model is loaded without explicit options (UI, API auto-load, CLI).</summary>
    public LoadOptions? Load { get; set; }
    /// <summary>Applied to requests for this model that do not set a parameter themselves.</summary>
    public GenerationOptions? Generation { get; set; }
}

/// <summary>
/// Per-model defaults ("gear dialog" in My Models). Keyed by the model's library id
/// (<c>publisher/repo/file.gguf</c>), persisted in <c>~/.llmhost/model-defaults.json</c>.
/// </summary>
public class ModelDefaultsStore
{
    private readonly object _gate = new();
    private Dictionary<string, ModelConfig>? _cache;

    private static string Key(string id) => id.Replace('\\', '/').Trim().ToLowerInvariant();

    private Dictionary<string, ModelConfig> Data()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(AppPaths.ModelDefaultsFile))
                _cache = JsonSerializer.Deserialize<Dictionary<string, ModelConfig>>(File.ReadAllText(AppPaths.ModelDefaultsFile), Json.Options);
        }
        catch { /* corrupt file: start empty */ }
        return _cache ??= new Dictionary<string, ModelConfig>();
    }

    private void Persist()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.ModelDefaultsFile, JsonSerializer.Serialize(_cache, Json.Options));
    }

    public ModelConfig? Get(string modelId)
    {
        lock (_gate)
        {
            var d = Data();
            if (d.TryGetValue(Key(modelId), out var c)) return c;
            // also match on the bare file name so an imported/moved file keeps its settings
            string file = Key(modelId);
            int slash = file.LastIndexOf('/');
            if (slash >= 0) file = file[(slash + 1)..];
            return d.FirstOrDefault(kv => kv.Key.EndsWith("/" + file, StringComparison.Ordinal) || kv.Key == file).Value;
        }
    }

    public LoadOptions? LoadFor(string modelId) => Get(modelId)?.Load?.Clone();
    public GenerationOptions? GenerationFor(string modelId) => Get(modelId)?.Generation?.Clone();

    public void Set(string modelId, LoadOptions? load, GenerationOptions? generation)
    {
        lock (_gate)
        {
            var d = Data();
            var cur = d.TryGetValue(Key(modelId), out var c) ? c : new ModelConfig();
            if (load != null) cur.Load = load;
            if (generation != null) cur.Generation = generation;
            d[Key(modelId)] = cur;
            Persist();
        }
    }

    /// <summary>Removes the load settings, the inference settings, or both (what = "load" | "generation" | null).</summary>
    public bool Clear(string modelId, string? what = null)
    {
        lock (_gate)
        {
            var d = Data();
            if (!d.TryGetValue(Key(modelId), out var c)) return false;
            if (what == "load") c.Load = null;
            else if (what == "generation") c.Generation = null;
            else { c.Load = null; c.Generation = null; }
            if (c.Load == null && c.Generation == null) d.Remove(Key(modelId));
            Persist();
            return true;
        }
    }

    public IReadOnlyDictionary<string, ModelConfig> All()
    {
        lock (_gate) return new Dictionary<string, ModelConfig>(Data());
    }
}
