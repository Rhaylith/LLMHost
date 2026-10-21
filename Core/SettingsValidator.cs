namespace LLMHostProject.Core;

/// <summary>One invalid field: the JSON path (<c>defaultLoad.contextSize</c>) and a message a person can act on.</summary>
public record FieldError(string Field, string Message);

/// <summary>Range / enum checks for <see cref="AppSettings"/>. Used by <c>PUT /api/settings</c> and reported at start-up.</summary>
public static class SettingsValidator
{
    private static readonly string[] Backends = { "auto", "vulkan", "cpu" };
    private static readonly string[] Guardrails = { "off", "balanced", "strict" };
    private static readonly string[] Devices = { "cpu", "gpu", "auto" };
    private static readonly string[] Tri = { "auto", "on", "off" };
    private static readonly string[] SplitModes = { "layer", "row", "none" };
    private static readonly string[] KvTypes = { "f16", "q8_0", "q4_0" };
    private static readonly string[] Efforts = { "off", "low", "medium", "high", "on" };

    public static List<FieldError> Validate(AppSettings s)
    {
        var e = new List<FieldError>();
        void Range(string field, double v, double min, double max, string what)
        {
            if (double.IsNaN(v) || v < min || v > max) e.Add(new(field, $"{what} must be between {min:0.##} and {max:0.##}."));
        }
        void OneOf(string field, string? v, string[] allowed, string what)
        {
            if (v == null || !allowed.Contains(v, StringComparer.OrdinalIgnoreCase)) e.Add(new(field, $"{what} must be one of: {string.Join(", ", allowed)}."));
        }

        if (string.IsNullOrWhiteSpace(s.Host)) e.Add(new("host", "Host must not be empty (use 127.0.0.1, or 0.0.0.0 for the LAN)."));
        Range("port", s.Port, 1, 65535, "Port");
        OneOf("backend", s.Backend, Backends, "Backend");
        OneOf("loadGuardrail", s.LoadGuardrail, Guardrails, "Load guardrail");
        Range("maxConcurrentDownloads", s.MaxConcurrentDownloads, 1, 8, "Concurrent downloads");
        if (!string.IsNullOrWhiteSpace(s.UpdateFeedUrl) &&
            !(Uri.TryCreate(s.UpdateFeedUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            e.Add(new("updateFeedUrl", "Update feed must be an http(s) URL."));
        if (!string.IsNullOrWhiteSpace(s.ModelsDirectory))
        {
            try { Path.GetFullPath(s.ModelsDirectory); }
            catch { e.Add(new("modelsDirectory", "Models folder is not a valid path.")); }
        }

        if (s.Logging == null) e.Add(new("logging", "Missing."));
        else
        {
            OneOf("logging.level", s.Logging.Level, new[] { "trace", "debug", "info", "warn", "error", "off" }, "Log level");
            OneOf("logging.native", s.Logging.Native, new[] { "debug", "info", "warn", "error", "off" }, "Native log level");
            Range("logging.retainDays", s.Logging.RetainDays, 0, 3650, "Log retention (days)");
            Range("logging.maxFileMb", s.Logging.MaxFileMb, 1, 1024, "Log file size (MB)");
        }
        if (s.PromptCache == null) e.Add(new("promptCache", "Missing."));
        else Range("promptCache.maxDiskMb", s.PromptCache.MaxDiskMb, 0, 10_000_000, "Prompt cache disk limit (MB)");
        if (s.Vision == null) e.Add(new("vision", "Missing."));
        else
        {
            Range("vision.maxImageEdge", s.Vision.MaxImageEdge, 0, 16384, "Maximum image edge");
            Range("vision.lowDetailEdge", s.Vision.LowDetailEdge, 32, 4096, "Low-detail image edge");
        }
        ValidateLoad(s.DefaultLoad, "defaultLoad", e, Range, OneOf);
        ValidateGeneration(s.DefaultGeneration, "defaultGeneration", e, Range, OneOf);
        return e;
    }

    private static void ValidateLoad(LoadOptions o, string p, List<FieldError> e,
        Action<string, double, double, double, string> range, Action<string, string?, string[], string> oneOf)
    {
        if (o == null) { e.Add(new(p, "Missing.")); return; }
        oneOf($"{p}.device", o.Device, Devices, "Device");
        oneOf($"{p}.flashAttentionMode", o.FlashAttentionMode, Tri, "Flash attention");
        oneOf($"{p}.splitMode", o.SplitMode, SplitModes, "Split mode");
        oneOf($"{p}.kvCacheTypeK", o.KvCacheTypeK, KvTypes, "K cache type");
        oneOf($"{p}.kvCacheTypeV", o.KvCacheTypeV, KvTypes, "V cache type");
        if (o.ContextSize != 0) range($"{p}.contextSize", o.ContextSize, 128, 4_000_000, "Context length");
        range($"{p}.maxConcurrent", o.MaxConcurrent, 1, 16, "Parallel requests");
        if (o.PromptTemplate != null)
            foreach (var problem in Inference.PromptTemplates.Check(o.PromptTemplate)) e.Add(new($"{p}.promptTemplate", problem));
        range($"{p}.draftTokens", o.DraftTokens, 0, 64, "Draft tokens");
        range($"{p}.draftMinP", o.DraftMinP, 0, 1, "Draft confidence");
        range($"{p}.batchSize", o.BatchSize, 1, 65536, "Batch size");
        range($"{p}.uBatchSize", o.UBatchSize, 1, 65536, "Micro-batch size");
        range($"{p}.threads", o.Threads, 0, 1024, "Threads");
        range($"{p}.batchThreads", o.BatchThreads, 0, 1024, "Batch threads");
        range($"{p}.ttlMinutes", o.TtlMinutes, 0, 525_600, "Idle time-to-live");
        if (o.GpuLayers is < 0) range($"{p}.gpuLayers", o.GpuLayers.Value, 0, 10_000, "GPU layers");
        if (o.TensorSplit != null && o.TensorSplit.Any(x => x < 0 || float.IsNaN(x))) range($"{p}.tensorSplit", -1, 0, 1e9, "Tensor split ratios");
    }

    private static void ValidateGeneration(GenerationOptions g, string p, List<FieldError> e,
        Action<string, double, double, double, string> range, Action<string, string?, string[], string> oneOf)
    {
        if (g == null) { e.Add(new(p, "Missing.")); return; }
        range($"{p}.temperature", g.Temperature, 0, 5, "Temperature");
        range($"{p}.topK", g.TopK, 0, 1_000_000, "Top-k");
        range($"{p}.topP", g.TopP, 0, 1, "Top-p");
        range($"{p}.minP", g.MinP, 0, 1, "Min-p");
        range($"{p}.typicalP", g.TypicalP, 0, 1, "Typical-p");
        range($"{p}.repeatPenalty", g.RepeatPenalty, 0.01, 10, "Repeat penalty");
        range($"{p}.frequencyPenalty", g.FrequencyPenalty, -2, 2, "Frequency penalty");
        range($"{p}.presencePenalty", g.PresencePenalty, -2, 2, "Presence penalty");
        range($"{p}.maxTokens", g.MaxTokens, -2, 10_000_000, "Max tokens");
        range($"{p}.topLogprobs", g.TopLogprobs, 0, 20, "Top log-probs");
        range($"{p}.mirostat", g.Mirostat, 0, 2, "Mirostat mode");
        range($"{p}.mirostatTau", g.MirostatTau, 0, 20, "Mirostat tau");
        range($"{p}.mirostatEta", g.MirostatEta, 0, 1, "Mirostat eta");
        range($"{p}.dryMultiplier", g.DryMultiplier, 0, 10, "DRY multiplier");
        range($"{p}.dryBase", g.DryBase, 1, 4, "DRY base");
        range($"{p}.dryAllowedLength", g.DryAllowedLength, 0, 10_000, "DRY allowed length");
        range($"{p}.dryPenaltyLastN", g.DryPenaltyLastN, -1, 10_000_000, "DRY penalty window");
        range($"{p}.xtcProbability", g.XtcProbability, 0, 1, "XTC probability");
        range($"{p}.xtcThreshold", g.XtcThreshold, 0, 1, "XTC threshold");
        range($"{p}.topNSigma", g.TopNSigma, -1, 10, "Top-n sigma");
        if (g.SamplerOrder != null && Inference.ConfigurableSamplingPipeline.UnknownStages(g.SamplerOrder).ToList() is { Count: > 0 } bad)
            e.Add(new($"{p}.samplerOrder", $"Unknown sampler stage(s): {string.Join(", ", bad)}. Use: {string.Join(", ", Inference.ConfigurableSamplingPipeline.StageNames)}."));
        if (g.ReasoningEffort != null) oneOf($"{p}.reasoningEffort", g.ReasoningEffort, Efforts, "Reasoning effort");
    }

    public static string Summary(IEnumerable<FieldError> errors) =>
        string.Join(" ", errors.Select(x => $"{x.Field}: {x.Message}"));
}
