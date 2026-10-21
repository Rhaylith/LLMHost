using System.Text.Json;
using System.Text.Json.Serialization;

namespace LLMHostProject.Core;

public static class AppPaths
{
    public static string Root { get; } = Environment.GetEnvironmentVariable("LLMHOST_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llmhost");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string PresetsFile => Path.Combine(Root, "presets.json");
    public static string ModelDefaultsFile => Path.Combine(Root, "model-defaults.json");
    public static string DownloadsFile => Path.Combine(Root, "downloads.json");
    public static string TokensFile => Path.Combine(Root, "tokens.json");
    public static string ChatsDir => Path.Combine(Root, "chats");
    /// <summary>Previous default (kept only so an old, incidentally persisted value is treated as "unset").</summary>
    public static string LegacyModelsDir => Path.Combine(Root, "models");
    /// <summary>Default models folder: "DownloadedModels" inside the working directory the app was started from.</summary>
    public static string DefaultModelsDir => Path.Combine(Directory.GetCurrentDirectory(), "DownloadedModels");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ChatsDir);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

/// <summary>How a model is loaded into memory (BlahLLMWrapper "Load" panel).</summary>
public class LoadOptions
{
    /// <summary>"cpu" (default), "gpu" (all layers) or "auto" (offload as many layers as VRAM allows).</summary>
    public string Device { get; set; } = "cpu";
    /// <summary>Explicit number of layers on the GPU. null = decided by <see cref="Device"/>.</summary>
    public int? GpuLayers { get; set; }
    public int MainGpu { get; set; }
    /// <summary>GPUs to use (indexes as listed by 'hardware'). Null or empty = automatic.</summary>
    public List<int>? GpuDevices { get; set; }
    /// <summary>Relative share of the model for each entry of <see cref="GpuDevices"/>. Null = proportional to memory.</summary>
    public List<float>? TensorSplit { get; set; }
    /// <summary>How a model is spread over several GPUs: "layer" (default), "row" or "none" (only <see cref="MainGpu"/>).</summary>
    public string SplitMode { get; set; } = "layer";
    /// <summary>Load even when the memory estimate says the model will not fit (skips the guardrail).</summary>
    public bool SkipFitCheck { get; set; }
    public uint ContextSize { get; set; } = 4096;
    /// <summary>Requests generated at the same time (continuous batching); the rest wait in a queue. 1 = one at a time.</summary>
    public int MaxConcurrent { get; set; } = 4;
    public uint BatchSize { get; set; } = 512;
    public uint UBatchSize { get; set; } = 512;
    /// <summary>CPU threads for generation. 0 = default (75% of the logical cores).</summary>
    public int Threads { get; set; }
    /// <summary>CPU threads for prompt processing. 0 = same as <see cref="Threads"/>.</summary>
    public int BatchThreads { get; set; }
    /// <summary>"auto" (default: llama.cpp enables flash attention when the device supports it), "on" or "off".</summary>
    public string FlashAttentionMode { get; set; } = "auto";
    /// <summary>Older settings files: true meant "on". Kept so they still work; <see cref="FlashAttentionMode"/> "off" wins.</summary>
    public bool FlashAttention { get; set; }
    /// <summary>true / false when forced, null for automatic.</summary>
    [JsonIgnore]
    public bool? FlashAttentionSetting => FlashAttentionMode.Equals("off", StringComparison.OrdinalIgnoreCase) ? false
        : FlashAttentionMode.Equals("on", StringComparison.OrdinalIgnoreCase) || FlashAttention ? true : null;
    public bool UseMmap { get; set; } = true;
    public bool UseMlock { get; set; }
    /// <summary>f16, q8_0 or q4_0.</summary>
    public string KvCacheTypeK { get; set; } = "f16";
    public string KvCacheTypeV { get; set; } = "f16";
    public float? RopeFrequencyBase { get; set; }
    public float? RopeFrequencyScale { get; set; }
    public bool Embeddings { get; set; }
    /// <summary>Load the multimodal projector (mmproj) when one sits next to the model, enabling image input.</summary>
    public bool Vision { get; set; } = true;
    /// <summary>Runtime this model prefers (see 'runtime list'). The native library is process wide: a mismatch is reported, not switched.</summary>
    public string? Runtime { get; set; }
    /// <summary>Explicit path of the mmproj file (default: auto-detected next to the model).</summary>
    public string? MmprojPath { get; set; }
    /// <summary>API identifier for the loaded model. Defaults to the file name.</summary>
    public string? Identifier { get; set; }
    /// <summary>Unload automatically after this many idle minutes (0 = never).</summary>
    public int TtlMinutes { get; set; }

    public LoadOptions Clone()
    {
        var c = (LoadOptions)MemberwiseClone();
        c.GpuDevices = GpuDevices != null ? new List<int>(GpuDevices) : null;
        c.TensorSplit = TensorSplit != null ? new List<float>(TensorSplit) : null;
        return c;
    }
}

/// <summary>Sampling / generation parameters (BlahLLMWrapper "Advanced configuration").</summary>
public class GenerationOptions
{
    public float Temperature { get; set; } = 0.7f;
    public int TopK { get; set; } = 40;
    public float TopP { get; set; } = 0.95f;
    public float MinP { get; set; } = 0.05f;
    public float TypicalP { get; set; } = 1.0f;
    public float RepeatPenalty { get; set; } = 1.1f;
    public int RepeatLastN { get; set; } = 64;
    public float FrequencyPenalty { get; set; }
    public float PresencePenalty { get; set; }
    /// <summary>-1 = until end-of-sequence or context is full.</summary>
    public int MaxTokens { get; set; } = -1;
    /// <summary>null = random seed.</summary>
    public uint? Seed { get; set; }
    public List<string> Stop { get; set; } = new();
    public string? SystemPrompt { get; set; }
    /// <summary>Raw GBNF grammar constraining the output.</summary>
    public string? Grammar { get; set; }
    /// <summary>Force the output to be valid JSON.</summary>
    public bool JsonMode { get; set; }
    /// <summary>JSON schema the output must follow (converted to a grammar).</summary>
    public string? JsonSchema { get; set; }
    public Dictionary<int, float>? LogitBias { get; set; }
    /// <summary>Show / keep <c>&lt;think&gt;</c> reasoning output (CLI and web UI; the API always returns it separately).</summary>
    public bool KeepReasoning { get; set; } = true;
    /// <summary>
    /// Reasoning effort: null = the model's default, "off", "low", "medium", "high" or "on". Mapped onto each model's
    /// template (enable_thinking switch, Harmony "Reasoning:" line) and, for low / medium, a thinking-token budget.
    /// </summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>Record the log-probability of every generated token (and the <see cref="TopLogprobs"/> best alternatives).</summary>
    public bool Logprobs { get; set; }
    public int TopLogprobs { get; set; }
    /// <summary>Keep the KV cache between requests and only evaluate the part of the prompt that changed.</summary>
    public bool CachePrompt { get; set; } = true;

    public GenerationOptions Clone()
    {
        var c = (GenerationOptions)MemberwiseClone();
        c.Stop = new List<string>(Stop);
        c.LogitBias = LogitBias != null ? new Dictionary<int, float>(LogitBias) : null;
        return c;
    }
}

public class AppSettings
{
    /// <summary>Format version of settings.json (see <see cref="Migrations"/>).</summary>
    public int SchemaVersion { get; set; } = Migrations.CurrentVersion(Migrations.Settings);

    /// <summary>
    /// Where models are stored. Null/empty (the default) means a "DownloadedModels" folder in the working directory.
    /// A relative path is resolved against the working directory.
    /// </summary>
    public string? ModelsDirectory { get; set; }

    [JsonIgnore]
    public string ResolvedModelsDirectory
    {
        get
        {
            var raw = ModelsDirectory;
            if (string.IsNullOrWhiteSpace(raw)) return AppPaths.DefaultModelsDir;
            var full = Path.GetFullPath(raw);
            // old versions persisted ~/.llmhost/models as an explicit value; treat it as the default
            if (string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(AppPaths.LegacyModelsDir), StringComparison.OrdinalIgnoreCase))
                return AppPaths.DefaultModelsDir;
            return full;
        }
    }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1234;
    public string? ApiKey { get; set; }
    /// <summary>Require an API token (see the Tokens page) on /v1 and /api requests.</summary>
    public bool RequireAuth { get; set; }
    public bool Cors { get; set; } = true;
    /// <summary>Serve the Anthropic Messages API (/v1/messages) alongside the OpenAI API.</summary>
    public bool AnthropicApi { get; set; } = true;
    /// <summary>auto, vulkan or cpu. Vulkan is the only supported GPU backend.</summary>
    public string Backend { get; set; } = "auto";
    public string? HuggingFaceToken { get; set; }
    public bool AutoLoadOnRequest { get; set; } = true;
    /// <summary>Downloads running at the same time; the rest wait in the queue.</summary>
    public int MaxConcurrentDownloads { get; set; } = 2;
    public bool VerboseNativeLog { get; set; }
    public LoggingSettings Logging { get; set; } = new();
    /// <summary>Write the last /v1/messages request to ~/.llmhost/debug/anthropic-last.json (to see what a client such as Claude Code really sends). Contains your prompts.</summary>
    public bool DebugRequests { get; set; }
    /// <summary>Send reasoning to Anthropic-API clients as ordinary text (inside a think tag) so it streams visibly; off = separate thinking blocks.</summary>
    public bool AnthropicReasoningAsText { get; set; } = true;
    /// <summary>When on, closing the window keeps the app (and its server) running in the notification area. Off by default: closing quits.</summary>
    public bool MinimizeToTray { get; set; }
    /// <summary>Look for a newer version at <see cref="UpdateFeedUrl"/> at start-up and every 12 hours. Off by default.</summary>
    public bool CheckUpdates { get; set; }
    /// <summary>URL of a small JSON document {"version","url","notes"} announcing the latest release. None by default.</summary>
    public string? UpdateFeedUrl { get; set; }
    /// <summary>"auto", "bundled:&lt;variant&gt;" or "user:&lt;name&gt;" (see 'runtime list'). Applied at start-up.</summary>
    public string Runtime { get; set; } = "auto";
    /// <summary>Re-check URL-installed runtimes for newer builds when the server starts.</summary>
    public bool AutoUpdateRuntimes { get; set; }
    /// <summary>
    /// Only count dedicated GPU memory when deciding how much of a model to offload. Integrated GPUs then
    /// stop borrowing system RAM, which keeps the rest of the machine responsive.
    /// </summary>
    public bool LimitToDedicatedGpuMemory { get; set; }
    /// <summary>"off" (never refuse), "balanced" (refuse loads that cannot fit) or "strict" (also refuse loads that would be tight).</summary>
    public string LoadGuardrail { get; set; } = "balanced";
    public LoadOptions DefaultLoad { get; set; } = new();
    public GenerationOptions DefaultGeneration { get; set; } = new();

    // ---- persistence ----
    private static readonly object Gate = new();
    private static AppSettings? _current;

    [JsonIgnore]
    public static AppSettings Current
    {
        get
        {
            lock (Gate) return _current ??= Load();
        }
    }

    /// <summary>The settings if they are already loaded, without triggering a load (safe to call from logging).</summary>
    internal static AppSettings? Loaded { get { lock (Gate) return _current; } }

    private static AppSettings Load()
    {
        AppPaths.EnsureCreated();
        Migrations.RunAll();
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                string text = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(text, Json.Options) ?? new();
                KeepUnknownKeys(text);
                foreach (var err in SettingsValidator.Validate(s))
                    LogBuffer.Add("settings", $"Invalid value in settings.json - {err.Field}: {err.Message}");
                return s;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not read settings: {ex.Message}");
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".invalid", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    /// <summary>Keys the app does not know (typos, newer versions) are dropped on save; keep them in settings.json.invalid.</summary>
    private static void KeepUnknownKeys(string json)
    {
        try
        {
            var unknown = Migrations.FindUnknownKeys(System.Text.Json.Nodes.JsonNode.Parse(json), typeof(AppSettings));
            if (unknown.Count == 0) return;
            var o = new System.Text.Json.Nodes.JsonObject();
            foreach (var (k, v) in unknown) o[k] = v;
            File.WriteAllText(AppPaths.SettingsFile + ".invalid", o.ToJsonString(Json.Options));
            LogBuffer.Add("settings", $"Ignoring unknown settings: {string.Join(", ", unknown.Keys)} (kept in settings.json.invalid).");
        }
        catch { }
    }

    public void Save()
    {
        lock (Gate)
        {
            AppPaths.EnsureCreated();
            SchemaVersion = Migrations.CurrentVersion(Migrations.Settings);
            Migrations.WriteAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json.Options));
        }
    }

    public void Replace(AppSettings other)
    {
        ModelsDirectory = other.ModelsDirectory; Host = other.Host; Port = other.Port; ApiKey = other.ApiKey; RequireAuth = other.RequireAuth;
        Cors = other.Cors; AnthropicApi = other.AnthropicApi; Backend = other.Backend;
        HuggingFaceToken = other.HuggingFaceToken; AutoLoadOnRequest = other.AutoLoadOnRequest;
        MaxConcurrentDownloads = Math.Clamp(other.MaxConcurrentDownloads, 1, 8);
        LimitToDedicatedGpuMemory = other.LimitToDedicatedGpuMemory;
        Runtime = string.IsNullOrWhiteSpace(other.Runtime) ? "auto" : other.Runtime;
        AutoUpdateRuntimes = other.AutoUpdateRuntimes;
        MinimizeToTray = other.MinimizeToTray; CheckUpdates = other.CheckUpdates; DebugRequests = other.DebugRequests; AnthropicReasoningAsText = other.AnthropicReasoningAsText;
        UpdateFeedUrl = string.IsNullOrWhiteSpace(other.UpdateFeedUrl) ? null : other.UpdateFeedUrl.Trim();
        LoadGuardrail = other.LoadGuardrail is "off" or "strict" ? other.LoadGuardrail : "balanced";
        VerboseNativeLog = other.VerboseNativeLog; Logging = other.Logging ?? new(); DefaultLoad = other.DefaultLoad; DefaultGeneration = other.DefaultGeneration;
        Save();
    }
}

/// <summary>Named bundles of generation parameters + system prompt (BlahLLMWrapper "presets").</summary>
public class PresetStore
{
    private readonly object _gate = new();

    public Dictionary<string, GenerationOptions> All()
    {
        lock (_gate) return Migrations.ReadItems<GenerationOptions>(AppPaths.PresetsFile, Migrations.Presets);
    }

    public void Save(string name, GenerationOptions opts)
    {
        lock (_gate)
        {
            var all = All();
            all[name] = opts;
            Migrations.WriteItems(AppPaths.PresetsFile, Migrations.Presets, all);
        }
    }

    public bool Delete(string name)
    {
        lock (_gate)
        {
            var all = All();
            if (!all.Remove(name)) return false;
            Migrations.WriteItems(AppPaths.PresetsFile, Migrations.Presets, all);
            return true;
        }
    }
}
