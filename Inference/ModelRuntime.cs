using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using LLMHostProject.Core;
using LLMHostProject.Models;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace LLMHostProject.Inference;

/// <summary>A model could not be loaded; <see cref="Kind"/> says why (corrupt, unsupported-architecture, out-of-memory, guardrail, missing, failed).</summary>
public class ModelLoadException : InvalidOperationException
{
    public string Kind { get; }
    public ModelLoadException(string kind, string message, Exception? inner = null) : base(message, inner) { Kind = kind; }
}

/// <summary>A chat turn. <see cref="Images"/> holds the raw bytes of attached images (vision models only).</summary>
public record ChatMessage(string Role, string Content, IReadOnlyList<byte[]>? Images = null);

/// <summary>One generated token with its log-probability and the most likely alternatives.</summary>
public record TokenLogprob(string Token, double Logprob, List<(string Token, double Logprob)> Top);

public class GenerationStats
{
    /// <summary>Filled while generating when <see cref="GenerationOptions.Logprobs"/> is set.</summary>
    public List<TokenLogprob> Logprobs { get; } = new();
    public int PromptTokens { get; set; }
    /// <summary>Prompt tokens served from the KV cache instead of being evaluated again.</summary>
    public int CachedTokens { get; set; }
    public int CompletionTokens { get; set; }
    public string FinishReason { get; set; } = "stop";
    /// <summary>The stop string that ended generation, if any.</summary>
    public string? StopSequence { get; set; }
    public double TimeToFirstTokenMs { get; set; }
    public double TotalMs { get; set; }
    public double TokensPerSecond => CompletionTokens > 0 && TotalMs > TimeToFirstTokenMs
        ? CompletionTokens / ((TotalMs - TimeToFirstTokenMs) / 1000.0)
        : 0;
}

/// <summary>A model that is loaded in memory and can generate text, chat and embeddings.</summary>
public sealed class ModelRuntime : IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly ModelParams _params;
    private LLamaContext? _ctx;
    private readonly MtmdWeights? _mtmd;
    private readonly object _mtmdLock = new();
    private readonly string _mediaMarker = "<__media__>";
    private readonly LLamaBatch _batch = new();
    private LLamaEmbedder? _embedder;

    public string Identifier { get; }
    public LocalModel Model { get; }
    public LoadOptions Options { get; }
    public int GpuLayers { get; }
    /// <summary>Generation threads actually used (default = 75% of logical cores).</summary>
    public int Threads => Hardware.EffectiveThreads(Options.Threads);
    public DateTime LoadedAt { get; } = DateTime.Now;
    public DateTime LastUsed { get; private set; } = DateTime.Now;
    public long Requests => Interlocked.Read(ref _requests);
    public uint ContextSize => _params.ContextSize ?? 0;
    public bool Busy => ActiveRequests > 0 || QueuedRequests > 0;

    /// <summary>True when a multimodal projector is loaded and the model accepts image input.</summary>
    public bool SupportsVision => _mtmd != null;
    /// <summary>True when the loaded projector also has an audio encoder (WAV / MP3 / FLAC input).</summary>
    public bool SupportsAudio => _mtmd is { SupportsAudio: true };
    /// <summary>What this loaded instance can do: the model's capabilities, with media input only when the projector really loaded.</summary>
    public ModelCapabilities Capabilities => Model.Capabilities with
    {
        Vision = SupportsVision,
        Audio = SupportsAudio,
    };
    public string? MmprojFile { get; }

    private ModelRuntime(string id, LocalModel model, LoadOptions options, int gpuLayers, LLamaWeights weights, ModelParams p,
        MtmdWeights? mtmd, string? mmproj, string marker)
    {
        _mtmd = mtmd; MmprojFile = mmproj; _mediaMarker = marker;
        Identifier = id; Model = model; Options = options; GpuLayers = gpuLayers; _weights = weights; _params = p;
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Warnings produced while loading (a retry with fewer GPU layers, capped context...).</summary>
    public List<string> LoadWarnings { get; } = new();
    public FitEstimate? Estimate { get; private set; }
    /// <summary>The GPUs / split this model was loaded with (null for CPU-only loads).</summary>
    public GpuPlan? Plan { get; private set; }

    public static int PlanGpuLayers(LocalModel model, LoadOptions o, GgufInfo? info)
    {
        if (!NativeBackend.GpuAvailable) return 0;
        if (o.GpuLayers.HasValue) return Math.Max(0, o.GpuLayers.Value);
        switch (o.Device.ToLowerInvariant())
        {
            case "cpu": return 0;
            case "gpu": return 999;
        }
        // auto: as many layers as the memory estimate (weights per layer + KV cache + compute buffers) allows
        try
        {
            var e = FitEstimator.Estimate(model, o, gpuBackend: true);
            return e.MaxGpuLayers < 0 ? 0 : e.MaxGpuLayers;
        }
        catch { return 999; }
    }

    private static readonly string[] OomMarkers =
    {
        "out of memory", "failed to allocate", "unable to allocate", "errorOutOfDeviceMemory", "ERROR_OUT_OF_DEVICE_MEMORY",
        "VK_ERROR_OUT_OF", "bad_alloc", "not enough memory", "cudaMalloc failed", "insufficient memory", "failed to create context",
    };

    private static bool LooksLikeOutOfMemory(Exception ex, string logs) =>
        OomMarkers.Any(m => ex.Message.Contains(m, StringComparison.OrdinalIgnoreCase) || logs.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static string RecentNativeLog() => string.Join('\n', LogBuffer.Tail(25));

    private static Exception Explain(LocalModel model, Exception ex, string logs, int layers)
    {
        var m = Regex.Match(logs, @"unknown model architecture: '([^']+)'", RegexOptions.IgnoreCase);
        if (m.Success)
            return new ModelLoadException("unsupported-architecture",
                $"'{model.FileName}' uses the architecture '{m.Groups[1].Value}', which this build of the inference engine does not support. " +
                "Try a different model, or a newer version of this app.");
        if (logs.Contains("tensor", StringComparison.OrdinalIgnoreCase) && logs.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || logs.Contains("wrong shape", StringComparison.OrdinalIgnoreCase) || logs.Contains("invalid", StringComparison.OrdinalIgnoreCase) && logs.Contains("gguf", StringComparison.OrdinalIgnoreCase))
            return new ModelLoadException("corrupt",
                $"'{model.FileName}' could not be read: the file looks damaged or incomplete. Download it again.");
        if (LooksLikeOutOfMemory(ex, logs))
            return new ModelLoadException("out-of-memory",
                $"Not enough memory to load '{model.FileName}'" + (layers > 0 ? $" even after reducing the GPU offload (started with {(layers >= 999 ? "all" : layers.ToString())} layers)" : "") +
                ". Use a smaller quantization, a smaller context size, or a quantized KV cache (q8_0 with flash attention).");
        return new ModelLoadException("failed", $"Loading '{model.FileName}' failed: {ex.Message}", ex);
    }

    private static GGMLType? ParseKv(string t) => t.ToLowerInvariant() switch
    {
        "q8_0" => GGMLType.GGML_TYPE_Q8_0,
        "q4_0" => GGMLType.GGML_TYPE_Q4_0,
        "f32" => GGMLType.GGML_TYPE_F32,
        _ => null,
    };

    private static ModelParams BuildParams(LocalModel model, LoadOptions o, int gpuLayers, bool embeddings, GpuPlan? plan = null)
    {
        var p = new ModelParams(model.Path)
        {
            ContextSize = o.ContextSize,
            GpuLayerCount = gpuLayers,
            MainGpu = o.MainGpu,
            BatchSize = o.BatchSize,
            UBatchSize = o.UBatchSize,
            UseMemorymap = o.UseMmap,
            UseMemoryLock = o.UseMlock,
            FlashAttention = o.FlashAttentionSetting,   // null = automatic (on where the backend supports it)
            Embeddings = embeddings,
            // with the CPU device selected make sure the GPU backend doesn't pick up big matmuls
            OpOffload = gpuLayers > 0,
        };
        // parallel requests: one sequence per slot, all sharing the same KV pool (so the context length is not divided)
        int seqs = embeddings ? 1 : o.SlotCount;
        p.SeqMax = (uint)seqs;
        if (seqs > 1) p.KVUnified = true;
        // default: 75% of the logical cores
        int threads = Hardware.EffectiveThreads(o.Threads);
        p.Threads = threads;
        p.BatchThreads = o.BatchThreads > 0 ? o.BatchThreads : threads;
        if (o.RopeFrequencyBase.HasValue) p.RopeFrequencyBase = o.RopeFrequencyBase.Value;
        if (o.RopeFrequencyScale.HasValue) p.RopeFrequencyScale = o.RopeFrequencyScale.Value;
        var k = ParseKv(o.KvCacheTypeK); var v = ParseKv(o.KvCacheTypeV);
        if (k.HasValue) p.TypeK = k.Value;
        if (v.HasValue) p.TypeV = v.Value;
        // quantized V cache requires flash attention
        if (v.HasValue && p.FlashAttention != true) p.FlashAttention = true;

        // multi-GPU: which devices, in which proportions
        if (plan != null && gpuLayers > 0)
        {
            p.MainGpu = plan.MainGpu;
            p.SplitMode = plan.SplitMode switch { "row" => GPUSplitMode.Row, "none" => GPUSplitMode.None, _ => GPUSplitMode.Layer };
            if (plan.TensorSplit.Length > 0) p.TensorSplits = new TensorSplitsCollection(plan.TensorSplit);
        }
        return p;
    }

    public static async Task<ModelRuntime> LoadAsync(LocalModel model, LoadOptions options, string identifier,
        IProgress<float>? progress, CancellationToken ct)
    {
        NativeBackend.Initialize();
        var warnings = new List<string>();

        // ---- the file itself: damaged / truncated / incomplete models fail here with a clear message
        GgufInfo info;
        try { info = FitEstimator.ReadModel(model.Path).Info; }
        catch (GgufFormatException ex) { throw new ModelLoadException("corrupt", $"'{model.FileName}' cannot be loaded: {ex.Message}", ex); }
        catch (FileNotFoundException ex) { throw new ModelLoadException("missing", ex.Message, ex); }
        if (model.Path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            throw new ModelLoadException("corrupt", "That file is an unfinished download (.part).");

        if (options.ContextSize == 0 || options.ContextSize > 1_000_000) options.ContextSize = 4096;
        if (info.ContextLength > 0 && options.ContextSize > info.ContextLength)
        {
            warnings.Add($"Context length capped to the model's maximum of {info.ContextLength}.");
            options.ContextSize = (uint)info.ContextLength;
        }

        int layers = PlanGpuLayers(model, options, info);

        // the native library is process wide: report (do not switch) when the model prefers another runtime
        if (!string.IsNullOrWhiteSpace(options.Runtime) && !options.Runtime.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var want = RuntimeManager.Find(options.Runtime);
            if (want == null)
                warnings.Add($"This model prefers the runtime '{options.Runtime}', which is not installed.");
            else if (!want.Active)
                warnings.Add($"This model prefers the runtime '{want.Name}' but the app runs '{NativeBackend.ActiveRuntimeId}'. " +
                             $"Run 'runtime select {want.Id}' and restart to use it.");
        }

        // ---- will it fit? (guardrail)
        FitEstimate? estimate = null;
        try { estimate = FitEstimator.Estimate(model, options, gpuBackend: NativeBackend.GpuAvailable, gpuLayersOverride: layers); }
        catch (Exception ex) { LogBuffer.Add("load", $"Memory estimate unavailable: {ex.Message}"); }
        if (estimate != null)
        {
            LogBuffer.Add("load", "Memory estimate: " + estimate.Describe() + $" [{estimate.Verdict}]");
            string rail = AppSettings.Current.LoadGuardrail;
            bool refuse = !options.SkipFitCheck && (estimate.Verdict == "wont-fit" && rail != "off" || estimate.Verdict == "tight" && rail == "strict");
            if (refuse) throw new ModelLoadException("guardrail", FitEstimator.RefusalMessage(model, estimate));
            if (estimate.Verdict != "fits") warnings.Add("Memory is tight: " + estimate.Describe());
            warnings.AddRange(estimate.Warnings);
        }

        // ---- load, retrying with fewer GPU layers when the device runs out of memory
        bool embeddingsMode = options.Embeddings || model.IsEmbedding;
        LLamaWeights? weights = null;
        LLamaContext? context = null;
        ModelParams p = null!;
        GpuPlan? plan = null;
        var sw = Stopwatch.StartNew();
        for (int attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                try { estimate = FitEstimator.Estimate(model, options, gpuBackend: NativeBackend.GpuAvailable, gpuLayersOverride: layers); } catch { }
            }
            plan = layers > 0 ? GpuPlanner.Plan(options, estimate, layers) : null;
            if (plan != null) foreach (var n in plan.Notes) { warnings.Add(n); LogBuffer.Add("load", n); }
            p = BuildParams(model, options, layers, embeddingsMode, plan);
            LogBuffer.Add("load", $"Loading {model.FileName} (device={(layers > 0 ? "gpu" : "cpu")}, gpu_layers={(layers >= 999 ? "all" : layers.ToString())}, ctx={p.ContextSize}, threads={p.Threads}, batch_threads={p.BatchThreads}, logical_cores={Environment.ProcessorCount})");
            try
            {
                weights = await LLamaWeights.LoadFromFileAsync(p, ct, progress);
                // create the context now so a KV-cache / compute-buffer allocation failure shows up here, not on the first request
                context = weights.CreateContext(p);
                break;
            }
            catch (OperationCanceledException) { weights?.Dispose(); throw; }
            catch (Exception ex)
            {
                weights?.Dispose(); weights = null; context = null;
                string logs = RecentNativeLog();
                bool oom = LooksLikeOutOfMemory(ex, logs);
                if (layers > 0 && attempt < 4 && oom)
                {
                    int reduced = layers >= 999 ? (int)Math.Max(1, info.BlockCount / 2) : layers / 2;
                    string note = $"Out of memory with {(layers >= 999 ? "all" : layers.ToString())} GPU layers; retrying with {(reduced <= 0 ? "CPU only" : reduced.ToString())}.";
                    LogBuffer.Add("load", note);
                    warnings.Add(note);
                    layers = reduced;
                    continue;
                }
                throw Explain(model, ex, logs, layers);
            }
        }
        LogBuffer.Add("load", $"Loaded {model.FileName} in {sw.Elapsed.TotalSeconds:0.0}s");

        // multimodal projector: enables image input
        MtmdWeights? mtmd = null;
        string? mmproj = null;
        string marker = "<__media__>";
        bool isEmbedding = options.Embeddings || model.IsEmbedding;
        string? mmprojPath = options.MmprojPath ?? model.MmprojPath;
        if (options.Vision && !isEmbedding && !string.IsNullOrWhiteSpace(mmprojPath) && File.Exists(mmprojPath))
        {
            try
            {
                var mp = MtmdContextParams.Default();
                mp.UseGpu = layers > 0;
                mp.NThreads = Hardware.EffectiveThreads(options.Threads);
                marker = mp.MediaMarker ?? marker;
                mtmd = await MtmdWeights.LoadFromFileAsync(mmprojPath, weights, mp, ct);
                mmproj = mmprojPath;
                LogBuffer.Add("load", $"Vision enabled with {Path.GetFileName(mmprojPath)} (vision={mtmd.SupportsVision})");
            }
            catch (Exception ex)
            {
                LogBuffer.Add("load", $"Could not load the multimodal projector {Path.GetFileName(mmprojPath)}: {ex.Message}. Continuing text-only.");
                mtmd = null;
            }
        }
        var rt = new ModelRuntime(identifier, model, options, layers, weights!, p, mtmd, mmproj, marker) { _ctx = context };
        rt.LoadWarnings.AddRange(warnings);
        rt.Estimate = estimate;
        rt.Plan = plan;
        await rt.RestoreAutoAsync();
        return rt;
    }

    // ------------------------------------------------------------------ prompts

    public int CountTokens(string text) => _weights.Tokenize(text, false, true, Encoding.UTF8).Length;

    /// <summary>Applies the model's own chat template (falls back to ChatML).</summary>
    public string ApplyChatTemplate(IReadOnlyList<ChatMessage> messages, bool addAssistant = true)
    {
        string prompt;
        try
        {
            var t = new LLamaTemplate(_weights) { AddAssistant = addAssistant };
            foreach (var m in messages) t.Add(m.Role, WithMarkers(m));
            prompt = Encoding.UTF8.GetString(t.Apply());
            if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("empty template");
        }
        catch
        {
            var sb = new StringBuilder();
            foreach (var m in messages) sb.Append("<|im_start|>").Append(m.Role).Append('\n').Append(WithMarkers(m)).Append("<|im_end|>\n");
            if (addAssistant) sb.Append("<|im_start|>assistant\n");
            prompt = sb.ToString();
        }
        return prompt;
    }

    /// <summary>
    /// Builds a prompt that fits the context window by dropping the oldest non-system turns first.
    /// </summary>
    public string BuildFittingPrompt(List<ChatMessage> messages, int reserveForOutput, out int droppedMessages)
        => BuildFittingPrompt(messages, reserveForOutput, out droppedMessages, out _);

    private ReasoningStyle? _style;
    /// <summary>How this model's chat template handles thinking (detected from the GGUF template).</summary>
    public ReasoningStyle ReasoningStyle
    {
        get
        {
            if (_style != null) return _style.Value;
            try { _style = Reasoning.Detect(FitEstimator.ReadModel(Model.Path).Info.ChatTemplate); }
            catch { _style = ReasoningStyle.None; }
            // older GGUF templates do not mention thinking although the model does it: go by the model's name
            if (_style == ReasoningStyle.None)
            {
                string n = (Model.Id + " " + Model.Name).ToLowerInvariant();
                if (new[] { "deepseek-r1", "deepseek_r1", "-r1-", "qwq", "qwen3", "think", "reason", "magistral" }.Any(n.Contains))
                    _style = ReasoningStyle.ThinkSwitch;
            }
            return _style.Value;
        }
    }

    private ToolFormat? _toolFormat;
    /// <summary>The tool-call convention of this model's chat template.</summary>
    public ToolFormat ToolFormat
    {
        get
        {
            if (_toolFormat != null) return _toolFormat.Value;
            try { _toolFormat = ToolFormats.Detect(FitEstimator.ReadModel(Model.Path).Info.ChatTemplate); }
            catch { _toolFormat = Inference.ToolFormat.Generic; }
            return _toolFormat.Value;
        }
    }

    /// <summary>True when the model can show a separate reasoning section.</summary>
    public bool SupportsReasoning => ReasoningStyle != ReasoningStyle.None;

    /// <summary>
    /// Same as above, additionally returning the images (in prompt order) that belong to the messages that were kept.
    /// Image tokens are counted with the real multimodal tokenizer so they are budgeted correctly.
    /// </summary>
    public string BuildFittingPrompt(List<ChatMessage> messages, int reserveForOutput, out int droppedMessages,
        out IReadOnlyList<byte[]> images, string? reasoningEffort = null)
    {
        droppedMessages = 0;
        // earlier reasoning is not sent back to the model (it would only use up the context window)
        var work = messages.Select(m => m.Role == "assistant" && m.Content.Contains(TextScanner.ThinkOpen)
            ? m with { Content = Reasoning.StripThink(m.Content) } : m).ToList();
        var hint = Reasoning.SystemHint(ReasoningStyle, reasoningEffort);
        if (hint != null)
        {
            int si = work.FindIndex(m => m.Role == "system");
            if (si >= 0) work[si] = work[si] with { Content = work[si].Content + "\n\n" + hint };
            else work.Insert(0, new ChatMessage("system", hint));
        }
        int budget = (int)ContextSize - Math.Max(16, reserveForOutput);
        while (true)
        {
            var prompt = Reasoning.ApplyToPrompt(ApplyChatTemplate(work), ReasoningStyle, reasoningEffort);
            var imgs = work.Where(m => m.Images != null).SelectMany(m => m.Images!).ToList();
            images = imgs;
            RequireAudioSupport(imgs);
            if (imgs.Count > 0 && _mtmd == null)
                throw new InvalidOperationException(
                    $"Model '{Identifier}' cannot read images: no multimodal projector (mmproj) is loaded. " +
                    "Download a vision model (it ships an mmproj file) or load it with vision enabled.");
            if (CountPromptTokens(prompt, imgs) <= budget) return prompt;
            int idx = work.FindIndex(m => m.Role != "system");
            // always keep the last message (the user's current turn)
            if (idx < 0 || idx >= work.Count - 1) return prompt;
            work.RemoveAt(idx);
            droppedMessages++;
        }
    }

    /// <summary>Fails with a clear message when the prompt carries audio but the loaded projector cannot hear.</summary>
    private void RequireAudioSupport(IReadOnlyList<byte[]>? media)
    {
        if (media is { Count: > 0 } && !SupportsAudio && media.Any(LLMHostProject.Server.ImageInput.IsAudio))
            throw new InvalidOperationException(
                $"Model '{Identifier}' cannot listen to audio: its multimodal projector has no audio encoder. Use an audio-capable model (e.g. Qwen2.5-Omni, Voxtral, Ultravox) with its mmproj file.");
    }

    private string WithMarkers(ChatMessage m)
    {
        if (m.Images is not { Count: > 0 }) return m.Content;
        var sb = new StringBuilder();
        foreach (var _ in m.Images) sb.Append(_mediaMarker).Append("\n");
        return sb.Append(m.Content).ToString();
    }

    /// <summary>Token count of a prompt, including the tokens the given images expand to.</summary>
    public int CountPromptTokens(string prompt, IReadOnlyList<byte[]>? images)
    {
        if (images is not { Count: > 0 } || _mtmd == null) return CountTokens(prompt);
        lock (_mtmdLock)
        {
            using var chunks = TokenizeMultimodal(prompt, images);
            return (int)chunks.CountTokens();
        }
    }

    // caller holds _mtmdLock
    private SafeMtmdInputChunks TokenizeMultimodal(string prompt, IReadOnlyList<byte[]> images)
    {
        _mtmd!.ClearMedia();
        foreach (var img in images) _mtmd.LoadMedia(img);
        var status = _mtmd.Tokenize(prompt, true, true, out var chunks);
        _mtmd.ClearMedia();
        if (status != 0 || chunks is null)
            throw new InvalidOperationException(
                "The images could not be matched to the prompt (unsupported image data or marker mismatch).");
        return chunks;
    }

    // ------------------------------------------------------------------ generation

    private static BaseSamplingPipeline BuildSampler(GenerationOptions g)
    {
        string? gbnf = g.Grammar;
        if (string.IsNullOrWhiteSpace(gbnf) && !string.IsNullOrWhiteSpace(g.JsonSchema)) gbnf = SchemaGrammar.FromSchema(g.JsonSchema);
        if (string.IsNullOrWhiteSpace(gbnf) && g.JsonMode) gbnf = SchemaGrammar.JsonObjectGrammar;
        return new ConfigurableSamplingPipeline(g, gbnf, g.Seed ?? (uint)Random.Shared.Next());
    }

    // ------------------------------------------------------------------ slots & continuous batching
    //
    // The context holds up to MaxConcurrent sequences ("slots") in one shared (unified) KV pool. Every slot keeps the
    // tokens of its last request, so a follow-up prompt that starts with the same tokens only evaluates the new tail.
    // One scheduler task owns the context: each iteration it builds ONE batch with the next token of every
    // generating slot plus a chunk of every prompt that is still being evaluated, decodes it, and samples per slot.
    // Requests therefore make progress together instead of queueing behind each other.

    private LLamaContext Context => _ctx ??= _weights.CreateContext(_params);

    private enum SlotState { Prefill, Generating }

    private sealed class Slot
    {
        public int Id;
        /// <summary>Tokens whose keys/values are resident in this sequence (the reusable prompt prefix).</summary>
        public readonly List<LLamaToken> Cache = new();
        public GenRequest? Req;
        public long LastUsed;
    }

    private sealed class GenRequest
    {
        public LLamaToken[] Tokens = Array.Empty<LLamaToken>();
        public string Prompt = "";
        public IReadOnlyList<byte[]>? Images;
        public bool Multimodal;
        /// <summary>The prompt has a token signature (images as stand-in tokens), so its KV cache can be reused and kept.</summary>
        public bool ImageReuse;
        public GenerationOptions G = null!;
        public GenerationStats Stats = null!;
        public CancellationToken Ct;
        public readonly System.Threading.Channels.Channel<TextSegment> Out =
            System.Threading.Channels.Channel.CreateUnbounded<TextSegment>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        public BaseSamplingPipeline Sampler = null!;
        public TextScanner Scanner = null!;
        public StreamingTokenDecoder? Decoder;
        public int PromptTokens, MaxTokens, Generated, ThinkTokens, ThinkBudget;
        public SlotState State;
        public int PromptIndex;                        // next prompt token to feed (text prompts)
        public int NextPos;                            // position of the next token in the sequence
        public List<LLamaToken> Feed = new();          // tokens for the next batch (generating)
        public List<LLamaToken> InFlight = new();      // tokens placed in the current batch
        public bool First = true, HitStop, Eos;
        public Stopwatch Sw = new();
    }

    private readonly List<Slot> _slots = new();
    private readonly Queue<GenRequest> _queue = new();
    private readonly object _qLock = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _ctxGate = new(1, 1);
    private readonly SemaphoreSlim _embedGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private volatile bool _disposed;
    private long _tick, _requests;

    /// <summary>Requests being generated right now.</summary>
    public int ActiveRequests { get { lock (_qLock) return _slots.Count(s => s.Req != null); } }
    public int QueuedRequests { get { lock (_qLock) return _queue.Count; } }
    /// <summary>Number of requests that can be generated at the same time.</summary>
    public int MaxConcurrent => Math.Max(1, Math.Clamp(Options.MaxConcurrent, 1, 16));

    /// <summary>One prompt-cache slot: how many tokens it holds and whether a request is running in it.</summary>
    public record CacheSlotInfo(int Id, int Tokens, bool Active, long LastUsed);

    public IReadOnlyList<CacheSlotInfo> CacheSlots
    {
        get { lock (_qLock) return _slots.Select(s => new CacheSlotInfo(s.Id, s.Cache.Count, s.Req != null, s.LastUsed)).ToList(); }
    }

    /// <summary>Largest number of cached prompt tokens held by any slot.</summary>
    public int CachedTokenCount { get { lock (_qLock) return _slots.Count == 0 ? 0 : _slots.Max(s => s.Cache.Count); } }

    private void EnsureSlots()
    {
        lock (_qLock)
        {
            if (_slots.Count > 0) return;
            for (int i = 0; i < Options.SlotCount; i++) _slots.Add(new Slot { Id = i });
        }
    }

    private void ClearSlot(Slot s)
    {
        try { _ctx?.NativeHandle.MemorySequenceRemove((LLamaSeqId)s.Id, 0, -1); } catch { /* disposed */ }
        s.Cache.Clear();
    }

    /// <summary>Drops the cached prompts of all idle slots so the next requests are evaluated from scratch.</summary>
    public async Task ClearCacheAsync()
    {
        EnsureSlots();
        await _ctxGate.WaitAsync();
        try { foreach (var s in _slots.Where(s => s.Req == null)) ClearSlot(s); }
        finally { _ctxGate.Release(); }
    }

    /// <summary>
    /// Streams the completion for an already formatted prompt as answer / reasoning segments. Requests to the same
    /// model are generated concurrently (up to <see cref="MaxConcurrent"/>); further ones wait in a queue.
    /// </summary>
    public async IAsyncEnumerable<TextSegment> StreamSegmentsAsync(string prompt, GenerationOptions g, GenerationStats stats,
        [EnumeratorCancellation] CancellationToken ct = default, IReadOnlyList<byte[]>? images = null)
    {
        if (_disposed) throw new ObjectDisposedException(Identifier);
        EnsureSlots();
        LastUsed = DateTime.Now;
        Interlocked.Increment(ref _requests);

        // a leading BOS text would be doubled because the tokenizer adds BOS itself
        foreach (var bos in new[] { "<s>", "<|begin_of_text|>", "<bos>" })
            if (prompt.StartsWith(bos, StringComparison.Ordinal)) { prompt = prompt[bos.Length..]; break; }

        bool multimodal = images is { Count: > 0 };
        RequireAudioSupport(images);
        if (multimodal && _mtmd == null)
            throw new InvalidOperationException(
                $"Model '{Identifier}' cannot read images: no multimodal projector (mmproj) is loaded.");

        var req = new GenRequest { Prompt = prompt, Images = images, Multimodal = multimodal, G = g, Stats = stats };
        int promptTokens;
        if (!multimodal)
        {
            req.Tokens = _weights.Tokenize(prompt, true, true, Encoding.UTF8);
            if (req.Tokens.Length == 0) throw new InvalidOperationException("The prompt is empty.");
            promptTokens = req.Tokens.Length;
        }
        else
        {
            lock (_mtmdLock)
            {
                using var chunks = TokenizeMultimodal(prompt, images!);
                promptTokens = (int)chunks.CountTokens();
                var sig = ImageSignature(chunks, images!);
                if (sig != null) { req.Tokens = sig; req.ImageReuse = true; }
            }
        }
        int room = (int)ContextSize - promptTokens - 1;
        if (room <= 0)
            throw new InvalidOperationException(
                $"Prompt{(multimodal ? " (with images)" : "")} is {promptTokens} tokens but the context window is only {ContextSize}. Reload with a larger context.");
        req.PromptTokens = promptTokens;
        req.MaxTokens = g.MaxTokens > 0 ? Math.Min(g.MaxTokens, room) : room;
        req.Sampler = BuildSampler(g);
        req.Scanner = new TextScanner(g.Stop, Reasoning.EndsInsideThink(prompt));
        req.ThinkBudget = Reasoning.Budget(g.ReasoningEffort);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        req.Ct = linked.Token;
        lock (_qLock)
        {
            _queue.Enqueue(req);
            _loop ??= Task.Run(SchedulerAsync);
        }
        _wake.Release();

        try
        {
            await foreach (var seg in req.Out.Reader.ReadAllAsync(ct)) yield return seg;
        }
        finally
        {
            // consumer is done (or gave up): make sure the scheduler stops working for this request
            if (!req.Out.Reader.Completion.IsCompleted) linked.Cancel();
            _wake.Release();
        }
    }

    private bool HasWork()
    {
        lock (_qLock) return _queue.Count > 0 || _slots.Any(s => s.Req != null);
    }

    private async Task SchedulerAsync()
    {
        while (!_disposed)
        {
            try { await _wake.WaitAsync(_stop.Token); } catch (OperationCanceledException) { break; }
            while (!_disposed && HasWork())
            {
                await _ctxGate.WaitAsync();
                try { await IterateAsync(); }
                catch (Exception ex) { LogBuffer.Add("gen", $"Scheduler error: {ex.Message}"); FailAll(ex); }
                finally { _ctxGate.Release(); }
            }
        }
    }

    private void FailAll(Exception ex)
    {
        List<GenRequest> victims;
        lock (_qLock)
        {
            victims = _slots.Where(s => s.Req != null).Select(s => s.Req!).ToList();
            victims.AddRange(_queue);
            _queue.Clear();
            foreach (var s in _slots) s.Req = null;
        }
        // the cache of a half-updated sequence cannot be trusted
        try { _ctx?.NativeHandle.MemoryClear(true); } catch { }
        foreach (var s in _slots) s.Cache.Clear();
        foreach (var r in victims)
        {
            try { r.Sampler.Dispose(); } catch { }
            r.Out.Writer.TryComplete(ex);
        }
    }

    private void Fail(Slot s, Exception ex)
    {
        var r = s.Req!;
        s.Req = null;
        ClearSlot(s);
        try { r.Sampler.Dispose(); } catch { }
        r.Out.Writer.TryComplete(ex);
    }

    private static int CommonPrefix(List<LLamaToken> cache, LLamaToken[] tokens)
    {
        int limit = Math.Min(cache.Count, tokens.Length - 1);     // at least one token must be evaluated
        int n = 0;
        while (n < limit && (int)cache[n] == (int)tokens[n]) n++;
        return n;
    }

    /// <summary>A reuse of fewer cached tokens than this is not worth overwriting another conversation's cache.</summary>
    internal const int MinReuseTokens = 32;

    /// <summary>
    /// Chooses the idle slot for a request: the one sharing the longest prompt prefix (at least <see cref="MinReuseTokens"/>);
    /// otherwise an empty slot, otherwise the least recently used one, so other conversations keep their cache.
    /// </summary>
    private Slot? PickSlot(GenRequest req)
    {
        Slot? best = null; int bestCommon = 0;
        int need = Math.Min(MinReuseTokens, Math.Max(1, req.Tokens.Length - 1));
        if (req.G.CachePrompt && (!req.Multimodal || req.ImageReuse))
            foreach (var s in _slots.Where(s => s.Req == null))
            {
                int common = CommonPrefix(s.Cache, req.Tokens);
                if (common >= need && (common > bestCommon || common == bestCommon && best != null && s.LastUsed < best.LastUsed))
                { best = s; bestCommon = common; }
            }
        if (best != null) return best;
        var idle = _slots.Where(s => s.Req == null).ToList();
        return idle.FirstOrDefault(s => s.Cache.Count == 0) ?? idle.OrderBy(s => s.LastUsed).FirstOrDefault();
    }

    private async Task IterateAsync()
    {
        var ctx = Context;

        // ---- requests whose consumer went away
        foreach (var s in _slots.Where(s => s.Req != null && s.Req.Ct.IsCancellationRequested).ToList())
        {
            var r = s.Req!;
            s.Req = null;
            try { r.Sampler.Dispose(); } catch { }
            if (r.Multimodal && !r.ImageReuse) ClearSlot(s);
            r.Out.Writer.TryComplete();
        }

        // ---- admit queued requests while there is a free slot and room in the shared KV pool
        while (true)
        {
            GenRequest? req;
            lock (_qLock) { if (!_queue.TryPeek(out req)) break; }
            if (req.Ct.IsCancellationRequested)
            {
                lock (_qLock) _queue.Dequeue();
                try { req.Sampler.Dispose(); } catch { }
                req.Out.Writer.TryComplete();
                continue;
            }

            // more cache slots than concurrent requests: only MaxConcurrent of them may generate at once
            if (_slots.Count(s => s.Req != null) >= MaxConcurrent) break;

            var slot = PickSlot(req);
            if (slot == null) break;

            // space in the pool: active requests reserve prompt + max tokens, idle slots hold their cache
            long need = req.PromptTokens + req.MaxTokens;
            var chosen = slot;
            long InUse() => _slots.Where(s => s != chosen).Sum(s => s.Req != null ? s.Req.PromptTokens + s.Req.MaxTokens : s.Cache.Count);
            while (InUse() + need > ContextSize)
            {
                var victim = _slots.Where(s => s != chosen && s.Req == null && s.Cache.Count > 0).OrderBy(s => s.LastUsed).FirstOrDefault();
                if (victim == null) break;
                ClearSlot(victim);
            }
            if (InUse() + need > ContextSize && _slots.Any(s => s != chosen && s.Req != null)) break;   // wait for others to finish

            lock (_qLock) _queue.Dequeue();
            try { Admit(chosen, req, ctx); }
            catch (Exception ex)
            {
                chosen.Req = null;
                ClearSlot(chosen);
                try { req.Sampler.Dispose(); } catch { }
                req.Out.Writer.TryComplete(ex);
            }
        }

        // ---- one batch for everybody
        _batch.Clear();
        int budget = (int)Math.Max(1, Options.BatchSize);
        var sample = new List<(Slot Slot, int Index)>();
        foreach (var s in _slots)
        {
            var r = s.Req;
            if (r == null || r.State != SlotState.Generating || r.Feed.Count == 0) continue;
            r.InFlight = r.Feed; r.Feed = new List<LLamaToken>();
            int idx = -1;
            for (int i = 0; i < r.InFlight.Count; i++)
                idx = _batch.Add(r.InFlight[i], r.NextPos + i, (LLamaSeqId)s.Id, i == r.InFlight.Count - 1);
            budget -= r.InFlight.Count;
            sample.Add((s, idx));
        }
        var prefill = _slots.Where(s => s.Req is { State: SlotState.Prefill }).ToList();
        for (int p = 0; p < prefill.Count; p++)
        {
            var s = prefill[p]; var r = s.Req!;
            int remaining = r.Tokens.Length - r.PromptIndex;
            int share = Math.Max(1, budget / (prefill.Count - p));
            int take = Math.Min(remaining, share);
            if (take <= 0) continue;
            r.InFlight = new List<LLamaToken>(take);
            int idx = -1;
            for (int i = 0; i < take; i++)
            {
                var t = r.Tokens[r.PromptIndex + i];
                r.InFlight.Add(t);
                bool last = r.PromptIndex + i == r.Tokens.Length - 1;
                idx = _batch.Add(t, r.NextPos + i, (LLamaSeqId)s.Id, last);
            }
            budget -= take;
            if (r.PromptIndex + take == r.Tokens.Length) sample.Add((s, idx));
        }
        if (_batch.TokenCount == 0)
        {
            await Task.Yield();
            return;
        }

        var dr = await ctx.DecodeAsync(_batch);
        if (dr != DecodeResult.Ok)
        {
            string why = dr == DecodeResult.NoKvSlot
                ? "The context window is full while several requests were running. Use a larger context or fewer parallel requests."
                : $"Decoding failed ({dr}).";
            FailAll(new InvalidOperationException(why));
            return;
        }

        // ---- the tokens are now part of each sequence
        foreach (var s in _slots)
        {
            var r = s.Req;
            if (r == null || r.InFlight.Count == 0) continue;
            if (!r.Multimodal || r.ImageReuse) s.Cache.AddRange(r.InFlight);
            r.NextPos += r.InFlight.Count;
            if (r.State == SlotState.Prefill) r.PromptIndex += r.InFlight.Count;
            r.InFlight = new List<LLamaToken>();
        }

        // ---- sample one token for every slot that has logits
        foreach (var (s, idx) in sample)
        {
            if (s.Req == null) continue;
            if (s.Req.State == SlotState.Prefill) s.Req.State = SlotState.Generating;
            try { SampleAndHandle(s, idx, ctx); }
            catch (Exception ex) { Fail(s, ex); }
        }
    }

    // ------------------------------------------------------------------ prompts with images: token signature and KV reuse
    //
    // A prompt with pictures is a list of chunks: text (real tokens) and images (N embeddings each). To compare it with what a
    // slot has cached, every image becomes N stand-in "tokens" derived from the picture's hash; they can never equal a real
    // token, so the usual longest-common-prefix logic works. An image is reused as a whole or not at all.

    private static int ImageHash(byte[] data)
    {
        return BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(data), 0);
    }

    /// <summary>Stand-in token of a picture's i-th embedding: always negative, so never a vocabulary id.</summary>
    internal static LLamaToken ImageToken(int hash, int i) => (LLamaToken)(-1 - (int)(((uint)hash * 2654435761u + (uint)i * 40503u) & 0x3FFFFFFF));

    /// <summary>The token signature of a tokenized prompt, or null when its KV cache cannot be reused (M-RoPE models, image count mismatch).</summary>
    private LLamaToken[]? ImageSignature(SafeMtmdInputChunks chunks, IReadOnlyList<byte[]> images)
    {
        if (_mtmd == null || _mtmd.UsesMRope) return null;
        var sig = new List<LLamaToken>((int)chunks.CountTokens());
        int next = 0;
        foreach (var chunk in chunks.Enumerate())
        {
            if (chunk.Type == SafeMtmdInputChunk.SafeMtmdInputChunkType.Text)
            {
                foreach (var t in chunk.GetTextTokensSpan()) sig.Add(t);
                continue;
            }
            if (next >= images.Count || (int)chunk.NPos != (int)chunk.NTokens) return null;
            int hash = ImageHash(images[next++]);
            for (int i = 0; i < (int)chunk.NTokens; i++) sig.Add(ImageToken(hash, i));
        }
        return next == images.Count ? sig.ToArray() : null;
    }

    /// <summary>
    /// Evaluates a tokenized multimodal prompt after the part the slot already holds. Returns the number of reused cells;
    /// <paramref name="nPast"/> is the position after the prompt, with logits ready for the last token.
    /// </summary>
    private int EvaluateWithReuse(Slot slot, GenRequest req, SafeMtmdInputChunks chunks, LLamaContext ctx, out int nPast)
    {
        var seq = (LLamaSeqId)slot.Id;
        int reuse = CommonPrefix(slot.Cache, req.Tokens);

        // an image is reused whole or not at all: step back to where the chunk containing the cut starts
        var layout = new List<(SafeMtmdInputChunk Chunk, int Start, int Cells, bool Text)>();
        int pos = 0;
        foreach (var c in chunks.Enumerate())
        {
            int n = (int)c.NTokens;
            layout.Add((c, pos, n, c.Type == SafeMtmdInputChunk.SafeMtmdInputChunkType.Text));
            pos += n;
        }
        foreach (var l in layout)
            if (!l.Text && reuse > l.Start && reuse < l.Start + l.Cells) { reuse = l.Start; break; }

        // drop the old tail from the sequence (some architectures cannot: then start over)
        if (reuse < slot.Cache.Count || reuse == 0)
        {
            bool ok = false;
            if (reuse > 0)
            {
                ctx.NativeHandle.MemorySequenceRemove(seq, reuse, -1);
                ok = (int)ctx.NativeHandle.MemorySequenceMaxPosition(seq) == reuse - 1;
            }
            if (!ok) { ClearSlot(slot); reuse = 0; }
        }
        if (reuse > 0 && reuse < slot.Cache.Count) slot.Cache.RemoveRange(reuse, slot.Cache.Count - reuse);

        nPast = reuse;
        for (int ci = 0; ci < layout.Count; ci++)
        {
            var (chunk, start, cells, isText) = layout[ci];
            int end = start + cells;
            if (end <= reuse) continue;
            bool last = ci == layout.Count - 1;
            if (isText)
            {
                var toks = chunk.GetTextTokensSpan();
                int from = Math.Max(0, reuse - start);
                int size = (int)Math.Max(1, Options.BatchSize);
                var batch = new LLamaBatch();
                for (int i = from; i < toks.Length; i += size)
                {
                    batch.Clear();
                    int stop = Math.Min(toks.Length, i + size);
                    for (int j = i; j < stop; j++) batch.Add(toks[j], nPast++, seq, last && j == toks.Length - 1);
                    if (ctx.Decode(batch) != DecodeResult.Ok)
                        throw new InvalidOperationException("Decoding the text around the images failed.");
                }
            }
            else
            {
                var er = _mtmd!.NativeHandle.EvaluateChunk(chunk.NativePtr, ctx.NativeHandle, ref nPast, slot.Id, (int)Options.BatchSize, last);
                if (er != 0) throw new InvalidOperationException($"Image evaluation failed (status {er}).");
            }
        }
        return reuse;
    }

    private void Admit(Slot slot, GenRequest req, LLamaContext ctx)
    {
        slot.Req = req;
        slot.LastUsed = ++_tick;
        req.Sw = Stopwatch.StartNew();
        var seq = (LLamaSeqId)slot.Id;

        if (!req.Multimodal)
        {
            int reuse = req.G.CachePrompt ? CommonPrefix(slot.Cache, req.Tokens) : 0;
            if (reuse < slot.Cache.Count)
            {
                bool removed = false;
                if (reuse > 0)
                {
                    ctx.NativeHandle.MemorySequenceRemove(seq, reuse, -1);
                    // some architectures (recurrent / sliding-window) cannot drop a tail: verify it really happened
                    removed = (int)ctx.NativeHandle.MemorySequenceMaxPosition(seq) == reuse - 1;
                }
                if (!removed) { ClearSlot(slot); reuse = 0; }
                else slot.Cache.RemoveRange(reuse, slot.Cache.Count - reuse);
            }
            req.Stats.CachedTokens = reuse;
            req.Stats.PromptTokens = req.Tokens.Length;
            req.PromptIndex = reuse;
            req.NextPos = reuse;
            req.State = SlotState.Prefill;
            // seed the repetition penalties with the end of the prompt
            int window = Math.Min(Math.Max(req.G.RepeatLastN, 0), req.Tokens.Length);
            for (int i = req.Tokens.Length - window; i < req.Tokens.Length; i++) req.Sampler.Accept(req.Tokens[i]);
            return;
        }

        // ---- multimodal prompt: images are encoded by the projector and evaluated together with the text.
        int nPast = 0, cached = 0;
        if (!req.ImageReuse || !req.G.CachePrompt)
        {
            // no usable token signature (M-RoPE models, mismatched images): the slot starts empty and is dropped afterwards
            ClearSlot(slot);
            lock (_mtmdLock)
            {
                using var chunks = TokenizeMultimodal(req.Prompt, req.Images!);
                var er = _mtmd!.EvaluateChunks(chunks, ctx.NativeHandle, ref nPast, slot.Id, (int)Options.BatchSize, true);
                if (er != 0) throw new InvalidOperationException($"Image/prompt evaluation failed (status {er}).");
            }
        }
        else
        {
            // images are part of the cached prefix: only what follows the shared start is evaluated
            lock (_mtmdLock)
            {
                using var chunks = TokenizeMultimodal(req.Prompt, req.Images!);
                cached = EvaluateWithReuse(slot, req, chunks, ctx, out nPast);
            }
            slot.Cache.Clear();
            slot.Cache.AddRange(req.Tokens);
        }
        req.Stats.CachedTokens = cached;
        req.Stats.PromptTokens = req.PromptTokens;
        req.NextPos = nPast;
        req.State = SlotState.Generating;
        SampleAndHandle(slot, -1, ctx);        // logits of the last evaluated token
    }

    private void SampleAndHandle(Slot slot, int logitIndex, LLamaContext ctx)
    {
        var r = slot.Req!;
        var token = r.Sampler.Sample(ctx.NativeHandle, logitIndex);
        if (r.G.Logprobs && !token.IsEndOfGeneration(_weights.Vocab)) RecordLogprob(r, ctx, logitIndex, token);
        HandleToken(slot, token, ctx);
    }

    /// <summary>Log-probability of the sampled token and of the most likely alternatives (log-softmax of the logits).</summary>
    private static void RecordLogprob(GenRequest r, LLamaContext ctx, int index, LLamaToken token)
    {
        var logits = ctx.NativeHandle.GetLogitsIth(index);
        float max = float.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++) if (logits[i] > max) max = logits[i];
        double sum = 0;
        for (int i = 0; i < logits.Length; i++) sum += Math.Exp(logits[i] - max);
        double lse = max + Math.Log(sum);

        int k = Math.Clamp(r.G.TopLogprobs, 0, 20);
        var vals = new float[k]; var ids = new int[k];
        Array.Fill(vals, float.NegativeInfinity);
        for (int i = 0; i < logits.Length && k > 0; i++)
        {
            float v = logits[i];
            if (v <= vals[k - 1]) continue;
            int j = k - 1;
            while (j > 0 && vals[j - 1] < v) { vals[j] = vals[j - 1]; ids[j] = ids[j - 1]; j--; }
            vals[j] = v; ids[j] = i;
        }
        var top = new List<(string, double)>();
        for (int i = 0; i < k; i++)
            if (!float.IsNegativeInfinity(vals[i])) top.Add((TokenText(ctx, (LLamaToken)ids[i]), vals[i] - lse));
        double chosen = logits[(int)token] - lse;
        lock (r.Stats.Logprobs) r.Stats.Logprobs.Add(new TokenLogprob(TokenText(ctx, token), chosen, top));
    }

    private static string TokenText(LLamaContext ctx, LLamaToken t)
    {
        try { var d = new StreamingTokenDecoder(ctx); d.Add(t); return d.Read(); } catch { return ""; }
    }

    private void HandleToken(Slot slot, LLamaToken token, LLamaContext ctx)
    {
        var r = slot.Req!;
        if (token.IsEndOfGeneration(_weights.Vocab)) { r.Eos = true; Finish(slot); return; }
        r.Generated++;

        r.Decoder ??= new StreamingTokenDecoder(ctx);
        r.Decoder.Add(token);
        var piece = r.Decoder.Read();
        if (r.First) { r.Stats.TimeToFirstTokenMs = r.Sw.Elapsed.TotalMilliseconds; r.First = false; }
        if (piece.Length > 0)
        {
            foreach (var seg in r.Scanner.Feed(piece)) r.Out.Writer.TryWrite(seg);
            if (r.Scanner.Stopped) { r.HitStop = true; Finish(slot); return; }
        }
        if (r.Generated >= r.MaxTokens) { Finish(slot); return; }

        // the sampled token goes into the next batch so the following one can be predicted (and stays cached)
        r.Feed.Add(token);

        // reasoning effort: after the budget the think block is closed on the model's behalf
        if (r.Scanner.InThink)
        {
            r.ThinkTokens++;
            if (r.ThinkBudget > 0 && r.ThinkTokens >= r.ThinkBudget)
            {
                r.Feed.AddRange(_weights.Tokenize("\n" + TextScanner.ThinkClose + "\n\n", false, true, Encoding.UTF8));
                r.Scanner.ForceCloseThink();
                LogBuffer.Add("gen", $"Reasoning budget of {r.ThinkBudget} tokens reached; thinking closed.");
            }
        }
    }

    private void Finish(Slot slot)
    {
        var r = slot.Req!;
        foreach (var seg in r.Scanner.Flush()) r.Out.Writer.TryWrite(seg);
        r.Stats.CompletionTokens = r.Generated;
        r.Stats.TotalMs = r.Sw.Elapsed.TotalMilliseconds;
        r.Stats.StopSequence = r.Scanner.MatchedStop;
        r.Stats.FinishReason = !r.HitStop && !r.Eos && r.Generated >= r.MaxTokens ? "length" : "stop";
        try { r.Sampler.Dispose(); } catch { }
        slot.Req = null;
        slot.LastUsed = ++_tick;
        if (!r.G.CachePrompt || (r.Multimodal && !r.ImageReuse)) ClearSlot(slot);
        r.Out.Writer.TryComplete();
    }

    /// <summary>
    /// Plain-text stream: reasoning is wrapped in &lt;think&gt; tags when <see cref="GenerationOptions.KeepReasoning"/>
    /// is set (CLI, web UI) and dropped otherwise. API handlers use <see cref="StreamSegmentsAsync"/> instead.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(string prompt, GenerationOptions g, GenerationStats stats,
        [EnumeratorCancellation] CancellationToken ct = default, IReadOnlyList<byte[]>? images = null)
    {
        bool open = false;
        await foreach (var seg in StreamSegmentsAsync(prompt, g, stats, ct, images))
        {
            if (seg.Kind == SegmentKind.Reasoning)
            {
                if (!g.KeepReasoning) continue;
                if (!open) { open = true; yield return TextScanner.ThinkOpen + "\n"; }
                yield return seg.Text;
            }
            else
            {
                if (open) { open = false; yield return "\n" + TextScanner.ThinkClose + "\n\n"; }
                yield return seg.Text;
            }
        }
        if (open) yield return "\n" + TextScanner.ThinkClose + "\n\n";
    }

    public async Task<string> CompleteAsync(string prompt, GenerationOptions g, GenerationStats stats, CancellationToken ct = default,
        IReadOnlyList<byte[]>? images = null)
    {
        var sb = new StringBuilder();
        await foreach (var t in StreamAsync(prompt, g, stats, ct, images)) sb.Append(t);
        return sb.ToString();
    }

    /// <summary>Complete answer and reasoning as separate strings.</summary>
    public async Task<(string Content, string Reasoning)> CompleteSegmentsAsync(string prompt, GenerationOptions g, GenerationStats stats,
        CancellationToken ct = default, IReadOnlyList<byte[]>? images = null)
    {
        var content = new StringBuilder(); var reasoning = new StringBuilder();
        await foreach (var seg in StreamSegmentsAsync(prompt, g, stats, ct, images))
            (seg.Kind == SegmentKind.Reasoning ? reasoning : content).Append(seg.Text);
        return (content.ToString(), reasoning.ToString());
    }

    // ------------------------------------------------------------------ saved prompt caches

    /// <summary>Slot whose cache is worth saving: idle, non-empty, the longest (or the given slot).</summary>
    private Slot? SlotToSave(int? slotId) =>
        _slots.Where(s => s.Req == null && s.Cache.Count > 0 && (slotId == null || s.Id == slotId))
              .OrderByDescending(s => s.Cache.Count).FirstOrDefault();

    private unsafe SavedCache SaveSlot(Slot s, string name)
    {
        string kv = PromptCacheStore.KvPath(Model, name);
        Directory.CreateDirectory(Path.GetDirectoryName(kv)!);
        var tokens = s.Cache.ToArray();
        nuint written;
        fixed (LLamaToken* p = tokens)
            written = NativeApi.llama_state_seq_save_file(Context.NativeHandle, kv, (LLamaSeqId)s.Id, p, (UIntPtr)tokens.Length);
        if (written == 0) { try { File.Delete(kv); } catch { } throw new InvalidOperationException("The prompt cache could not be written."); }
        var meta = new SavedCache(name, tokens.Length, (long)written, DateTime.UtcNow, Model.Id, Options.KvCacheTypeK, Options.KvCacheTypeV);
        PromptCacheStore.WriteMeta(kv, meta);
        PromptCacheStore.EnforceLimit(Math.Max(0, AppSettings.Current.PromptCache.MaxDiskMb) * 1024L * 1024L);
        return meta;
    }

    /// <summary>Writes the longest idle prompt cache (or the one in <paramref name="slotId"/>) to disk under <paramref name="name"/>.</summary>
    public async Task<SavedCache> SaveCacheAsync(string name, int? slotId = null)
    {
        EnsureSlots();
        PromptCacheStore.KvPath(Model, name);          // validates the name
        await _ctxGate.WaitAsync();
        try
        {
            var slot = SlotToSave(slotId) ?? throw new InvalidOperationException("There is no idle prompt cache to save yet.");
            return SaveSlot(slot, name);
        }
        finally { _ctxGate.Release(); }
    }

    private unsafe int RestoreInto(Slot s, string name)
    {
        string kv = PromptCacheStore.KvPath(Model, name);
        var meta = PromptCacheStore.ReadMeta(kv);
        if (!File.Exists(kv) || meta == null) throw new FileNotFoundException($"No saved prompt cache called '{name}' for this model.");
        if (!string.Equals(meta.KvTypeK, Options.KvCacheTypeK, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(meta.KvTypeV, Options.KvCacheTypeV, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The saved cache was made with a {meta.KvTypeK}/{meta.KvTypeV} KV cache; this model uses {Options.KvCacheTypeK}/{Options.KvCacheTypeV}.");
        if (meta.Tokens >= ContextSize)
            throw new InvalidOperationException($"The saved cache holds {meta.Tokens} tokens but the context window is only {ContextSize}.");

        // make room in the shared pool: other idle caches are recycled, oldest first
        while (_slots.Where(o => o != s).Sum(o => o.Req != null ? o.Req.PromptTokens + o.Req.MaxTokens : o.Cache.Count) + meta.Tokens > ContextSize)
        {
            var victim = _slots.Where(o => o != s && o.Req == null && o.Cache.Count > 0).OrderBy(o => o.LastUsed).FirstOrDefault();
            if (victim == null) throw new InvalidOperationException("Not enough free context to restore this cache while other requests are running.");
            ClearSlot(victim);
        }
        ClearSlot(s);
        var tokens = new LLamaToken[Math.Max(meta.Tokens, 1)];
        nuint count, read;
        fixed (LLamaToken* p = tokens)
            read = NativeApi.llama_state_seq_load_file(Context.NativeHandle, kv, (LLamaSeqId)s.Id, p, (UIntPtr)tokens.Length, out count);
        if (read == 0 || (int)count != meta.Tokens) { ClearSlot(s); throw new InvalidOperationException("The saved prompt cache could not be loaded (different model or damaged file)."); }
        s.Cache.AddRange(tokens.Take((int)count));
        s.LastUsed = ++_tick;
        File.SetLastWriteTimeUtc(kv, DateTime.UtcNow);
        return (int)count;
    }

    /// <summary>Loads a saved cache into an idle slot (an empty one if there is, else the least recently used). Returns its token count.</summary>
    public async Task<int> RestoreCacheAsync(string name)
    {
        EnsureSlots();
        PromptCacheStore.KvPath(Model, name);
        await _ctxGate.WaitAsync();
        try
        {
            var idle = _slots.Where(s => s.Req == null).ToList();
            var slot = idle.FirstOrDefault(s => s.Cache.Count == 0) ?? idle.OrderBy(s => s.LastUsed).FirstOrDefault()
                       ?? throw new InvalidOperationException("All cache slots are busy; try again in a moment.");
            return RestoreInto(slot, name);
        }
        finally { _ctxGate.Release(); }
    }

    private const string AutoPrefix = "auto-";

    /// <summary>On unload: keeps the idle caches (most recently used first) as auto-N files so the next load starts warm.</summary>
    private void AutoSave()
    {
        try
        {
            if (!AppSettings.Current.PromptCache.AutoSave || Options.Embeddings || Model.IsEmbedding) return;
            var dir = PromptCacheStore.DirFor(Model);
            if (Directory.Exists(dir)) foreach (var old in Directory.EnumerateFiles(dir, AutoPrefix + "*")) try { File.Delete(old); } catch { }
            int n = 0;
            foreach (var s in _slots.Where(s => s.Req == null && s.Cache.Count >= MinReuseTokens).OrderByDescending(s => s.LastUsed))
                SaveSlot(s, AutoPrefix + n++);
            if (n > 0) LogBuffer.Add("cache", $"{Identifier}: saved {n} prompt cache(s) to disk.");
        }
        catch (Exception ex) { LogBuffer.Add("cache", $"{Identifier}: could not save the prompt cache: {ex.Message}"); }
    }

    /// <summary>After load: brings back the caches written by <see cref="AutoSave"/>.</summary>
    internal async Task RestoreAutoAsync()
    {
        try
        {
            if (!AppSettings.Current.PromptCache.AutoSave || Options.Embeddings || Model.IsEmbedding) return;
            var dir = PromptCacheStore.DirFor(Model);
            if (!Directory.Exists(dir)) return;
            int restored = 0;
            foreach (var kv in Directory.EnumerateFiles(dir, AutoPrefix + "*.kv").OrderBy(f => f, StringComparer.Ordinal))
            {
                try { await RestoreCacheAsync(Path.GetFileNameWithoutExtension(kv)); restored++; }
                catch (Exception ex) { LogBuffer.Add("cache", $"{Identifier}: skipped {Path.GetFileName(kv)}: {ex.Message}"); }
            }
            if (restored > 0) LogBuffer.Add("cache", $"{Identifier}: restored {restored} prompt cache(s) from disk.");
        }
        catch { }
    }

    public IReadOnlyList<SavedCache> SavedCaches() => PromptCacheStore.List(Model);

    // ------------------------------------------------------------------ embeddings

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        await _embedGate.WaitAsync(ct);
        try
        {
            LastUsed = DateTime.Now; Interlocked.Increment(ref _requests);
            _embedder ??= new LLamaEmbedder(_weights, BuildParams(Model, Options, GpuLayers, true, Plan));
            var results = await _embedder.GetEmbeddings(text, ct);
            if (results.Count == 0) return Array.Empty<float>();
            var v = new float[results[0].Length];
            foreach (var r in results) for (int i = 0; i < v.Length; i++) v[i] += r[i] / results.Count;
            double norm = Math.Sqrt(v.Sum(x => (double)x * x));
            if (norm > 0) for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
            return v;
        }
        finally { _embedGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _wake.Release();
        try { _loop?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        // idle prompt caches are written before FailAll wipes them
        _ctxGate.Wait();
        try { AutoSave(); } finally { _ctxGate.Release(); }
        FailAll(new ObjectDisposedException(Identifier));
        _ctxGate.Wait();
        try
        {
            _embedder?.Dispose();
            _ctx?.Dispose();
            _mtmd?.Dispose();
            _weights.Dispose();
        }
        finally { _ctxGate.Release(); }
    }
}
