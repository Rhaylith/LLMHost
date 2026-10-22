using System.Text.Json;
using System.Text.Json.Serialization;
using LLMHostProject.Inference;

namespace LLMHostProject.Core;

public static class AppPaths
{
    public static string Root { get; } = Environment.GetEnvironmentVariable("LLMHOST_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llmhost");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string PresetsFile => Path.Combine(Root, "presets.json");
    public static string PresetsDir => Path.Combine(Root, "presets");
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
    /// <summary>
    /// Share of the model's layers to put on the GPU, 0 to 1 (1 = all, 0 = none), instead of counting layers. <see cref="GpuLayers"/>
    /// wins when both are set. The layer count is worked out from the model when it is loaded (the output layer counts as one more).
    /// </summary>
    public float? GpuRatio { get; set; }

    /// <summary>
    /// Applies the common <c>--gpu</c> / <c>gpu</c> notation: "off" or "cpu" (no GPU), "max" or "all" (every layer), "auto" (as many as
    /// fit), or a ratio such as "0.5" (half of the layers).
    /// </summary>
    public void SetGpu(string value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        GpuRatio = null;
        switch (v)
        {
            case "off": case "cpu": case "none": Device = "cpu"; return;
            case "max": case "all": case "gpu": case "": Device = "gpu"; return;
            case "auto": Device = "auto"; return;
        }
        if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ratio) || ratio < 0 || ratio > 1)
            throw new ArgumentException($"GPU offload must be off, max, auto or a ratio between 0 and 1 (got '{value}').");
        Device = ratio >= 1 ? "gpu" : ratio <= 0 ? "cpu" : "gpu";
        if (ratio is > 0 and < 1) GpuRatio = (float)ratio;
    }

    /// <summary>The ratio asked for:<see cref="GpuRatio"/>, or a number written in <see cref="Device"/> (for example "0.5").</summary>
    public bool TryGetRatio(out double ratio)
    {
        if (GpuRatio.HasValue) { ratio = GpuRatio.Value; return true; }
        return double.TryParse(Device, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ratio);
    }

    /// <summary>Layers on the GPU as asked for by <see cref="GpuLayers"/> or a ratio; null when the choice is left to <see cref="Device"/>.</summary>
    public int? RequestedGpuLayers(long blockCount)
    {
        if (GpuLayers.HasValue) return Math.Max(0, GpuLayers.Value);
        return TryGetRatio(out var r) ? LayersForRatio(r, blockCount) : null;
    }

    /// <summary>
    /// Layers for a ratio of a model with <paramref name="blockCount"/> repeating layers: ratio × (blocks + 1), rounded; everything
    /// (999) at 1 or when rounding reaches the output layer, 0 at 0. Null when the layer count of the model is unknown.
    /// </summary>
    public static int? LayersForRatio(double ratio, long blockCount)
    {
        if (double.IsNaN(ratio)) return null;
        if (ratio >= 1) return 999;
        if (ratio <= 0) return 0;
        if (blockCount <= 0) return null;
        long total = blockCount + 1;
        long n = (long)Math.Round(ratio * total, MidpointRounding.AwayFromZero);
        return n >= total ? 999 : (int)n;
    }
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
    /// <summary>
    /// Conversations whose evaluated prompt is kept in memory so they resume without re-processing (prompt cache slots).
    /// 0 = automatic (parallel requests + 2). Never fewer than <see cref="MaxConcurrent"/>. The slots share the context
    /// window, so idle ones are recycled (least recently used first) when a request needs the room.
    /// </summary>
    public int CacheSlots { get; set; }
    /// <summary>
    /// Speculative decoding: library id (or path) of a small model with the same vocabulary that guesses tokens the main model then
    /// verifies in one pass. null = off. Output is identical, only faster; it applies while a single request is generating.
    /// </summary>
    public string? DraftModel { get; set; }
    /// <summary>Tokens the draft model proposes per step (0 = default 6).</summary>
    public int DraftTokens { get; set; }
    /// <summary>The draft stops proposing when it is less sure than this about its next token.</summary>
    public float DraftMinP { get; set; } = 0.5f;
    /// <summary>Replaces the chat template stored in the model file (Jinja text or manual prefixes / suffixes). Default: the model's own.</summary>
    public PromptTemplateSettings PromptTemplate { get; set; } = new();
    /// <summary>Mixture-of-experts models: experts used per token (0 = the model's own number). Fewer = faster and less accurate.</summary>
    public int MoeExpertCount { get; set; }
    /// <summary>Keep the expert weights of the first N layers in system memory (-1 = all layers, 0 = off). Attention and cache stay on the GPU, so big MoE models fit.</summary>
    public int MoeCpuLayers { get; set; }
    /// <summary>Embedding / reranking models: how token vectors are combined into one: auto (the model's own, mean for ordinary language models), none, mean, cls, last or rank (reranker score).</summary>
    public string Pooling { get; set; } = "auto";
    /// <summary>Scale embedding vectors to length 1 (what similarity search expects).</summary>
    public bool Normalize { get; set; } = true;
    /// <summary>Tokens evaluated per step when embedding (0 = the context length, at most 8192). An input must fit in one step.</summary>
    public int EmbedBatchSize { get; set; }
    /// <summary>LoRA adapters applied on top of the model (library id or file path, with a strength). They must be made for the same architecture.</summary>
    public List<LoraSetting> Loras { get; set; } = new();
    /// <summary>Advanced: put tensors whose name matches a regular expression into a given buffer ("CPU" or a device buffer name).</summary>
    public List<TensorOverrideSetting> TensorOverrides { get; set; } = new();
    [JsonIgnore]
    public int SlotCount => Math.Min(16, Math.Max(Math.Clamp(MaxConcurrent, 1, 16), CacheSlots > 0 ? CacheSlots : MaxConcurrent + 2));
    public uint BatchSize { get; set; } = 512;
    public uint UBatchSize { get; set; } = 512;
    /// <summary>CPU threads for generation. 0 = default (75% of the logical cores).</summary>
    public int Threads { get; set; }
    /// <summary>CPU threads for prompt processing. 0 = same as <see cref="Threads"/>.</summary>
    public int BatchThreads { get; set; }
    /// <summary>
    /// Restricts the compute threads to some processors: a list such as "0-7,16-23" (machine-wide numbers, see /api/hardware), "node:1" for
    /// one NUMA node, "physical" for one thread per physical core. Empty = any. Applies to the whole process; the model loaded last wins.
    /// </summary>
    public string? CpuAffinity { get; set; }
    /// <summary>Priority of the compute threads: normal (default), high or low.</summary>
    public string ThreadPriority { get; set; } = "normal";
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
        c.PromptTemplate = PromptTemplate?.Clone() ?? new PromptTemplateSettings();
        c.Loras = Loras?.Select(l => new LoraSetting { Id = l.Id, Scale = l.Scale }).ToList() ?? new();
        c.TensorOverrides = TensorOverrides?.Select(t => new TensorOverrideSetting { Pattern = t.Pattern, Buffer = t.Buffer }).ToList() ?? new();
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
    /// <summary>Hard limit of reasoning tokens before thinking is closed (0 = the limit that goes with <see cref="ReasoningEffort"/>). Set by the Anthropic <c>thinking.budget_tokens</c>.</summary>
    public int ThinkBudgetTokens { get; set; }
    /// <summary>Record the log-probability of every generated token (and the <see cref="TopLogprobs"/> best alternatives).</summary>
    public bool Logprobs { get; set; }
    public int TopLogprobs { get; set; }
    /// <summary>
    /// What to do when the conversation is longer than the context window: dropOldest (default), truncateMiddle, rollingWindow,
    /// stopAtLimit or error. rollingWindow also lets an answer run past the window by discarding the oldest cached tokens.
    /// </summary>
    public string ContextOverflow { get; set; } = "dropOldest";
    /// <summary>rollingWindow: tokens at the start that are never discarded (0 = the first 32).</summary>
    public int KeepFirstTokens { get; set; }
    /// <summary>Mirostat mode: 0 = off, 1 or 2. When on it replaces top-k / top-p / min-p / typical / XTC (temperature still applies).</summary>
    public int Mirostat { get; set; }
    /// <summary>Mirostat target entropy (tau) and learning rate (eta).</summary>
    public float MirostatTau { get; set; } = 5f;
    public float MirostatEta { get; set; } = 0.1f;
    /// <summary>DRY ("don't repeat yourself") penalty strength; 0 = off. Typical: 0.8.</summary>
    public float DryMultiplier { get; set; }
    public float DryBase { get; set; } = 1.75f;
    /// <summary>Repeats up to this many tokens long are free; longer ones are penalised exponentially.</summary>
    public int DryAllowedLength { get; set; } = 2;
    /// <summary>Tokens scanned for repeats: -1 = the whole context, 0 = off.</summary>
    public int DryPenaltyLastN { get; set; } = -1;
    /// <summary>Strings that reset DRY's sequence matching. null = newline, ':', '"' and '*'.</summary>
    public List<string>? DrySequenceBreakers { get; set; }
    /// <summary>XTC ("exclude top choices"): with this probability, drop all but the least likely of the tokens above the threshold. 0 = off.</summary>
    public float XtcProbability { get; set; }
    public float XtcThreshold { get; set; } = 0.1f;
    /// <summary>Keep only tokens within this many standard deviations of the top logit. 0 or less = off.</summary>
    public float TopNSigma { get; set; }
    /// <summary>
    /// Order of the sampler stages: any of penalties, dry, top_n_sigma, top_k, typ_p, top_p, min_p, xtc, temperature.
    /// null or empty = that default order; stages left out are skipped. (Tail-free sampling was removed from llama.cpp.)
    /// </summary>
    public List<string>? SamplerOrder { get; set; }
    /// <summary>Keep the KV cache between requests and only evaluate the part of the prompt that changed.</summary>
    public bool CachePrompt { get; set; } = true;

    public GenerationOptions Clone()
    {
        var c = (GenerationOptions)MemberwiseClone();
        c.Stop = new List<string>(Stop);
        c.LogitBias = LogitBias != null ? new Dictionary<int, float>(LogitBias) : null;
        c.DrySequenceBreakers = DrySequenceBreakers != null ? new List<string>(DrySequenceBreakers) : null;
        c.SamplerOrder = SamplerOrder != null ? new List<string>(SamplerOrder) : null;
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
    /// <summary>More library folders that are read like the main one (models are found there, downloads and imports go to <see cref="ModelsDirectory"/>).</summary>
    public List<string> ExtraModelsDirectories { get; set; } = new();

    /// <summary>All library folders, the download target first. The first folder wins when the same id exists in two of them.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ModelRoots
    {
        get
        {
            var roots = new List<string> { ResolvedModelsDirectory };
            foreach (var raw in ExtraModelsDirectories ?? new())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full;
                try { full = Path.GetFullPath(raw); } catch { continue; }
                if (!roots.Any(r => string.Equals(Path.TrimEndingDirectorySeparator(r), Path.TrimEndingDirectorySeparator(full), StringComparison.OrdinalIgnoreCase))) roots.Add(full);
            }
            return roots;
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
    /// <summary>Optional address of a newer staff-picks list (catalog.json format). Empty = use only the built-in list.</summary>
    public string? CatalogUrl { get; set; }
    /// <summary>Look for newer versions of downloaded models once a day (needs network access to Hugging Face). Off by default.</summary>
    public bool CheckModelUpdates { get; set; }
    /// <summary>After the first answer of a chat, ask the loaded model for a short title (skipped while it is busy).</summary>
    public bool AutoTitleChats { get; set; } = true;
    /// <summary>Key combinations that differ from the defaults, by action id (see <see cref="Shortcuts"/>); an empty text switches the shortcut off.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    public bool AutoLoadOnRequest { get; set; } = true;
    /// <summary>Just-in-time loading of models that a request names but that are not loaded (see <see cref="JitSettings"/>).</summary>
    public JitSettings Jit { get; set; } = new();
    /// <summary>Allow-list, request limits and HTTPS (see <see cref="NetworkSettings"/>).</summary>
    public NetworkSettings Network { get; set; } = new();
    /// <summary>Serve /metrics without an API token.</summary>
    public bool MetricsPublic { get; set; }
    /// <summary>Linking your own devices (see <see cref="LinkSettings"/>).</summary>
    public LinkSettings Link { get; set; } = new();
    /// <summary>How much of the web interface is shown: user, power or developer (controls are hidden, never disabled).</summary>
    public string UiMode { get; set; } = "power";
    /// <summary>Downloads running at the same time; the rest wait in the queue.</summary>
    public int MaxConcurrentDownloads { get; set; } = 2;
    public bool VerboseNativeLog { get; set; }
    public LoggingSettings Logging { get; set; } = new();
    public PromptCacheSettings PromptCache { get; set; } = new();
    /// <summary>Open the prompt-template section of the load dialog by default.</summary>
    public bool AlwaysShowTemplate { get; set; }
    public VisionSettings Vision { get; set; } = new();
    public RagSettings Rag { get; set; } = new();
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
                s.OpenToken();
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

    /// <summary>
    /// Turns the token read from settings.json into plain text. A token found in clear text (written by an older version) is
    /// protected right away by saving the file again; one that cannot be opened (another Windows user) is dropped with a log line.
    /// </summary>
    internal void OpenToken()
    {
        if (string.IsNullOrEmpty(HuggingFaceToken)) return;
        if (SecretStore.IsProtected(HuggingFaceToken))
        {
            HuggingFaceToken = SecretStore.Unprotect(HuggingFaceToken);
            if (HuggingFaceToken == null) LogBuffer.Add("settings", "The saved Hugging Face token could not be decrypted (it belongs to another Windows user); enter it again.");
        }
        else
        {
            try { Save(); LogBuffer.Add("settings", "The Hugging Face token was stored in clear text; it is now encrypted for this Windows user."); } catch { }
        }
    }

    public void Save()
    {
        lock (Gate)
        {
            AppPaths.EnsureCreated();
            SchemaVersion = Migrations.CurrentVersion(Migrations.Settings);
            var node = JsonSerializer.SerializeToNode(this, Json.Options)!.AsObject();
            if (node["huggingFaceToken"] is System.Text.Json.Nodes.JsonValue tv && tv.TryGetValue<string>(out var plain) && !string.IsNullOrEmpty(plain) && !SecretStore.IsProtected(plain))
                node["huggingFaceToken"] = SecretStore.Protect(plain);               // never written in clear text
            Migrations.WriteAtomic(AppPaths.SettingsFile, node.ToJsonString(Json.Options));
        }
    }

    public void Replace(AppSettings other)
    {
        ModelsDirectory = other.ModelsDirectory; ExtraModelsDirectories = other.ExtraModelsDirectories ?? new(); Host = other.Host; Port = other.Port; ApiKey = other.ApiKey; RequireAuth = other.RequireAuth;
        Cors = other.Cors; AnthropicApi = other.AnthropicApi; Backend = other.Backend;
        CatalogUrl = other.CatalogUrl; CheckModelUpdates = other.CheckModelUpdates; AutoTitleChats = other.AutoTitleChats; Shortcuts = other.Shortcuts ?? new(); HuggingFaceToken = other.HuggingFaceToken; AutoLoadOnRequest = other.AutoLoadOnRequest; Jit = other.Jit ?? new(); Network = other.Network ?? new(); MetricsPublic = other.MetricsPublic; Link = other.Link ?? new(); UiMode = other.UiMode is "user" or "developer" ? other.UiMode : "power";
        MaxConcurrentDownloads = Math.Clamp(other.MaxConcurrentDownloads, 1, 8);
        LimitToDedicatedGpuMemory = other.LimitToDedicatedGpuMemory;
        Runtime = string.IsNullOrWhiteSpace(other.Runtime) ? "auto" : other.Runtime;
        AutoUpdateRuntimes = other.AutoUpdateRuntimes;
        MinimizeToTray = other.MinimizeToTray; CheckUpdates = other.CheckUpdates; DebugRequests = other.DebugRequests; AnthropicReasoningAsText = other.AnthropicReasoningAsText;
        UpdateFeedUrl = string.IsNullOrWhiteSpace(other.UpdateFeedUrl) ? null : other.UpdateFeedUrl.Trim();
        LoadGuardrail = other.LoadGuardrail is "off" or "strict" ? other.LoadGuardrail : "balanced";
        VerboseNativeLog = other.VerboseNativeLog; Logging = other.Logging ?? new(); PromptCache = other.PromptCache ?? new(); Vision = other.Vision ?? new(); AlwaysShowTemplate = other.AlwaysShowTemplate; Rag = other.Rag ?? new(); DefaultLoad = other.DefaultLoad; DefaultGeneration = other.DefaultGeneration;
        Save();
    }
}


/// <summary>Image input options (settings.json → <c>vision</c>).</summary>
public class VisionSettings
{
    /// <summary>Images are scaled down so their longest edge is at most this many pixels before they are encoded. 0 = keep the original size.</summary>
    public int MaxImageEdge { get; set; }
    /// <summary>Longest edge used for images sent with <c>detail: "low"</c>.</summary>
    public int LowDetailEdge { get; set; } = 512;
    /// <summary>Path of ffmpeg.exe for video input (blank = next to the app, then the PATH).</summary>
    public string? FfmpegPath { get; set; }
}

/// <summary>Places the tensors whose name matches <see cref="Pattern"/> (regular expression, searched anywhere in the name) in <see cref="Buffer"/>.</summary>
public class TensorOverrideSetting
{
    public string Pattern { get; set; } = "";
    /// <summary>"CPU" (system memory) or the name of a device buffer type.</summary>
    public string Buffer { get; set; } = "CPU";
}

/// <summary>One LoRA adapter to apply: <see cref="Id"/> is a library id, file name or file path; <see cref="Scale"/> its strength (1 = as trained, 0 = off).</summary>
public class LoraSetting
{
    public string Id { get; set; } = "";
    public float Scale { get; set; } = 1f;
}

/// <summary>Chat-with-documents retrieval options (settings.json → <c>rag</c>).</summary>
public class RagSettings
{
    /// <summary>A reranking model (library id) that re-orders the best chunks before they go to the chat model. Empty = off.</summary>
    public string? RerankModel { get; set; }
    /// <summary>How many of the best chunks the reranker looks at.</summary>
    public int RerankCandidates { get; set; } = 20;
}

/// <summary>How models that are loaded on demand are handled: how long they stay, how many may be loaded, and what is unloaded to make room.</summary>
public class JitSettings
{
    /// <summary>Load a model when a request names it (together with <see cref="AppSettings.AutoLoadOnRequest"/>).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Idle minutes after which a model loaded on demand is unloaded (0 = never). A model with its own idle time keeps it.</summary>
    public int DefaultTtlMinutes { get; set; }
    /// <summary>Unload idle on-demand models when a new load would not fit or would exceed <see cref="MaxLoadedModels"/>. Models loaded by hand are never evicted.</summary>
    public bool AutoEvict { get; set; } = true;
    /// <summary>Most models that may be loaded at once when loading on demand (0 = no limit).</summary>
    public int MaxLoadedModels { get; set; }
    /// <summary>lru (least recently used first) | oldest (loaded longest ago first).</summary>
    public string EvictOrder { get; set; } = "lru";
}
