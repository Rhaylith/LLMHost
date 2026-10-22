using LLMHostProject.Core;
using LLMHostProject.Models;

namespace LLMHostProject.Inference;

/// <summary>Keeps track of loaded models (several can be resident at once) and handles idle unloading.</summary>
public class ModelManager : IDisposable
{
    private readonly Dictionary<string, ModelRuntime> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly System.Threading.Timer _idleTimer;
    private readonly object _gate = new();

    public ModelLibrary Library { get; }
    public event Action? Changed;
    /// <summary>Raised with a 0..1 value while a model is being loaded (id, progress).</summary>
    public event Action<string, float>? LoadProgress;

    public ModelDefaultsStore Defaults { get; }

    public ModelManager(ModelLibrary library, ModelDefaultsStore? defaults = null)
    {
        Library = library;
        Defaults = defaults ?? new ModelDefaultsStore();
        _idleTimer = new System.Threading.Timer(_ => UnloadIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public IReadOnlyList<ModelRuntime> Loaded
    {
        get { lock (_gate) return _loaded.Values.ToList(); }
    }

    public ModelRuntime? Get(string id)
    {
        lock (_gate) return _loaded.TryGetValue(id, out var r) ? r : null;
    }

    public async Task<ModelRuntime> LoadAsync(string modelQuery, LoadOptions? options = null, CancellationToken ct = default, bool allowDuplicate = false)
    {
        var model = Library.Find(modelQuery)
                    ?? throw new FileNotFoundException($"No local model matches '{modelQuery}'. Use 'models' to list downloaded models.");
        // explicit options win; otherwise the model's own saved defaults, otherwise the global defaults
        options = (options ?? Defaults.LoadFor(model.Id) ?? AppSettings.Current.DefaultLoad).Clone();
        string id = options.Identifier ?? Path.GetFileNameWithoutExtension(model.FileName);

        // asking for another instance of a loaded model: name it automatically ("name:2", "name:3"...)
        if (allowDuplicate && Get(id) != null)
        {
            if (options.Identifier != null)
                throw new InvalidOperationException($"'{id}' is already loaded. Choose another identifier, or leave it out to get an automatic one.");
            id = NextFreeIdentifier(id);
        }

        var existing = Get(id);
        if (existing != null) return existing;

        await _loadGate.WaitAsync(ct);
        try
        {
            if (allowDuplicate && options.Identifier == null && Get(id) != null) id = NextFreeIdentifier(Path.GetFileNameWithoutExtension(model.FileName));
            existing = Get(id);
            if (existing != null) return existing;
            var progress = new Progress<float>(p => LoadProgress?.Invoke(id, p));
            var rt = await ModelRuntime.LoadAsync(model, options, id, progress, ct);
            lock (_gate) _loaded[id] = rt;
            Changed?.Invoke();
            return rt;
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>The pool name of an instance: its identifier without a trailing ":2", ":3"...</summary>
    public static string PoolName(string identifier)
    {
        int colon = identifier.LastIndexOf(':');
        return colon > 0 && colon < identifier.Length - 1 && identifier[(colon + 1)..].All(char.IsDigit) ? identifier[..colon] : identifier;
    }

    private string NextFreeIdentifier(string baseId)
    {
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseId}:{n}";
            if (Get(candidate) == null) return candidate;
        }
    }

    /// <summary>The least busy of several instances of the same model (fewest running + waiting requests; ties go to the longest idle one).</summary>
    public static ModelRuntime LeastLoaded(IEnumerable<ModelRuntime> instances) =>
        instances.OrderBy(r => r.ActiveRequests + r.QueuedRequests).ThenBy(r => r.LastUsed).First();

    /// <summary>
    /// Loaded instances a request for <paramref name="requested"/> may use: one named exactly (also "name:2" for that single instance),
    /// otherwise every instance of the model called that (by pool name, library id or file name).
    /// </summary>
    public List<ModelRuntime> Instances(string requested)
    {
        var loaded = Loaded;
        // "name:2" addresses that one instance
        if (PoolName(requested) != requested)
            return loaded.Where(r => r.Identifier.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToList();
        // "name" addresses the whole pool: the first instance and every "name:N"
        var pool = loaded.Where(r => PoolName(r.Identifier).Equals(requested, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pool.Count > 0) return pool;
        return loaded.Where(r => r.Model.Id.Equals(requested, StringComparison.OrdinalIgnoreCase)
                                 || r.Model.FileName.Equals(requested, StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileNameWithoutExtension(r.Model.FileName).Equals(requested, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool Unload(string id)
    {
        ModelRuntime? rt;
        lock (_gate)
        {
            if (!_loaded.Remove(id, out rt)) return false;
        }
        rt.Dispose();
        LogBuffer.Add("load", $"Unloaded {id}");
        Changed?.Invoke();
        return true;
    }

    public void UnloadAll()
    {
        foreach (var r in Loaded) Unload(r.Identifier);
    }

    /// <summary>
    /// Resolves the model for a request: exact loaded id, the only loaded model, or (if enabled) loads it from disk.
    /// </summary>
    public async Task<ModelRuntime> ResolveAsync(string? requested, bool embeddings = false, CancellationToken ct = default)
    {
        var loaded = Loaded;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var instances = Instances(requested);
            if (instances.Count > 0) return RequireUsable(LeastLoaded(instances), embeddings);
        }
        else
        {
            var candidates = loaded.Where(r => embeddings ? r.Model.IsEmbedding || r.Options.Embeddings : !r.Model.IsEmbedding).ToList();
            if (candidates.Count >= 1) return candidates.OrderByDescending(r => r.LastUsed).First();
            if (loaded.Count >= 1 && embeddings) return loaded[0];
        }

        if (AppSettings.Current.AutoLoadOnRequest && !string.IsNullOrWhiteSpace(requested))
        {
            var target = Library.Find(requested);
            var opts = (target != null ? Defaults.LoadFor(target.Id) : null) ?? AppSettings.Current.DefaultLoad.Clone();
            if (embeddings) opts.Embeddings = true;
            return RequireUsable(await LoadAsync(requested, opts, ct), embeddings);
        }
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(requested)
            ? "No model is loaded. Load one first (CLI: /load <model>)."
            : $"Model '{requested}' is not loaded.");
    }

    /// <summary>Embedding and reranking models cannot write text: say so instead of failing somewhere inside.</summary>
    private static ModelRuntime RequireUsable(ModelRuntime rt, bool embeddings)
    {
        if (!embeddings && (rt.Model.IsEmbedding || rt.Options.Embeddings))
            throw new InvalidOperationException(
                $"'{rt.Identifier}' is {(rt.IsCrossEncoder ? "a reranking" : "an embedding")} model and cannot generate text. " +
                $"Use {(rt.IsCrossEncoder ? "/v1/rerank" : "/v1/embeddings")} with it, or pick a chat model.");
        return rt;
    }

    private void UnloadIdle()
    {
        foreach (var r in Loaded)
        {
            if (r.Options.TtlMinutes > 0 && !r.Busy && DateTime.Now - r.LastUsed > TimeSpan.FromMinutes(r.Options.TtlMinutes))
            {
                LogBuffer.Add("load", $"{r.Identifier} idle for {r.Options.TtlMinutes} min, unloading");
                Unload(r.Identifier);
            }
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        UnloadAll();
    }
}
