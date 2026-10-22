using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LLMHostProject.Core;
using LLMHostProject.Models;

namespace LLMHostProject.Inference;

/// <summary>Memory the model needs on one device, against what that device can offer.</summary>
public record DeviceFit(string Name, bool IsGpu, long NeedBytes, long BudgetBytes)
{
    public double Ratio => BudgetBytes > 0 ? (double)NeedBytes / BudgetBytes : double.PositiveInfinity;
}

/// <summary>Result of <see cref="FitEstimator.Estimate"/>: where the memory goes and whether it fits.</summary>
public class FitEstimate
{
    public long WeightsBytes { get; set; }
    public long KvCacheBytes { get; set; }
    public long ComputeBytes { get; set; }
    public long ProjectorBytes { get; set; }
    /// <summary>Speculative-decoding draft model (weights + its own cache), when one is configured.</summary>
    public long DraftBytes { get; set; }
    /// <summary>LoRA adapter files that are applied on top of the model.</summary>
    public long AdapterBytes { get; set; }
    /// <summary>Weights this load shares with an instance that is already loaded (not counted again).</summary>
    public long SharedWeightsBytes { get; set; }
    /// <summary>Sum of everything that lands on GPUs / in system memory.</summary>
    public long GpuBytes { get; set; }
    public long CpuBytes { get; set; }
    public int BlockCount { get; set; }
    /// <summary>Layers that will really be offloaded (repeating layers, capped at the block count).</summary>
    public int OffloadedLayers { get; set; }
    public bool OutputOnGpu { get; set; }
    public long RamTotalBytes { get; set; }
    public long RamAvailableBytes { get; set; }
    public List<DeviceFit> Devices { get; set; } = new();
    /// <summary>"fits", "tight" or "wont-fit".</summary>
    public string Verdict { get; set; } = "fits";
    public List<string> Warnings { get; set; } = new();
    /// <summary>The most layers (of <see cref="BlockCount"/>) the GPU memory budget can take; -1 when no GPU is involved.</summary>
    public int MaxGpuLayers { get; set; } = -1;
    /// <summary>A context length that fits when the requested one does not (0 = no suggestion needed).</summary>
    public uint SuggestedContext { get; set; }
    public bool Moe { get; set; }
    public long TotalBytes => GpuBytes + CpuBytes;

    public string Describe()
    {
        string f(long b) => Hardware.FormatBytes(b);
        var parts = new List<string> { SharedWeightsBytes > 0 ? $"weights shared with a loaded instance ({f(SharedWeightsBytes)})" : $"weights {f(WeightsBytes)}", $"KV cache {f(KvCacheBytes)}", $"compute {f(ComputeBytes)}" };
        if (ProjectorBytes > 0) parts.Add($"vision projector {f(ProjectorBytes)}");
        if (DraftBytes > 0) parts.Add($"draft model {f(DraftBytes)}");
        if (AdapterBytes > 0) parts.Add($"LoRA adapters {f(AdapterBytes)}");
        string where = string.Join("; ", Devices.Select(d => $"{d.Name}: {f(d.NeedBytes)} of {f(d.BudgetBytes)}"));
        return string.Join(" + ", parts) + (where.Length > 0 ? " → " + where : "");
    }
}

/// <summary>
/// Estimates the memory a model needs before it is loaded: exact weight sizes per layer from the GGUF tensor table,
/// KV cache (with sliding-window layers), compute buffers and the vision projector. Integrated GPUs are handled as
/// a mix of dedicated memory and shared system RAM.
/// </summary>
public static class FitEstimator
{
    private record Cached(DateTime Stamp, long Length, GgufInfo Info, List<TensorEntry> Tensors);
    private static readonly ConcurrentDictionary<string, Cached> Cache = new();

    /// <summary>Header + tensor table of a model (all shards), cached per file version.</summary>
    public static (GgufInfo Info, List<TensorEntry> Tensors) ReadModel(string path)
    {
        var fi = new FileInfo(path);
        if (Cache.TryGetValue(path, out var c) && c.Stamp == fi.LastWriteTimeUtc && c.Length == fi.Length) return (c.Info, c.Tensors);

        var files = GgufInfo.ShardFiles(path);
        GgufInfo? first = null;
        var all = new List<TensorEntry>();
        foreach (var f in files)
        {
            if (!File.Exists(f)) throw new GgufFormatException($"Part of a split model is missing: {Path.GetFileName(f)}");
            var info = GgufInfo.Read(f, out var t);
            first ??= info;
            all.AddRange(t);
        }
        Cache[path] = new Cached(fi.LastWriteTimeUtc, fi.Length, first!, all);
        return (first!, all);
    }

    private static double ElementBytes(string t) => t.ToLowerInvariant() switch
    {
        "q8_0" => 34.0 / 32, "q4_0" => 18.0 / 32, "q4_1" => 20.0 / 32, "q5_0" => 22.0 / 32, "q5_1" => 24.0 / 32, "f32" => 4.0, _ => 2.0,
    };

    /// <summary>Fraction of layers that use a sliding attention window, and how large that window is (0 = none).</summary>
    private static (double LocalFraction, long Window) SlidingWindow(GgufInfo info) => info.Architecture switch
    {
        var a when a.StartsWith("gemma3") => (5.0 / 6, info.SlidingWindow > 0 ? info.SlidingWindow : 1024),
        "gemma2" => (0.5, info.SlidingWindow > 0 ? info.SlidingWindow : 4096),
        "cohere2" => (0.75, info.SlidingWindow > 0 ? info.SlidingWindow : 4096),
        _ => (0, 0),
    };

    private static bool IsRecurrent(string arch) => arch is "mamba" or "mamba2" or "rwkv6" or "rwkv7" or "rwkv6qwen2" or "arwkv7";

    /// <summary>The GPUs a load with these options would use (empty = CPU only).</summary>
    public static List<GpuInfo> SelectDevices(LoadOptions o, HardwareInfo hw, bool gpuBackend)
    {
        var none = new List<GpuInfo>();
        if (!gpuBackend || hw.Gpus.Count == 0) return none;
        if (o.GpuLayers is 0) return none;
        if (o.GpuLayers is null && o.TryGetRatio(out var wanted) && wanted <= 0) return none;
        if (o.GpuLayers is null && !o.TryGetRatio(out _) && o.Device.Equals("cpu", StringComparison.OrdinalIgnoreCase)) return none;
        if (o.GpuDevices is { Count: > 0 })
            return o.GpuDevices.Select(i => hw.Gpus.FirstOrDefault(g => g.Index == i)).Where(g => g != null).Select(g => g!).ToList();
        if (o.SplitMode.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new List<GpuInfo> { hw.Gpus.FirstOrDefault(g => g.Index == o.MainGpu) ?? hw.BestGpu! };
        // automatic: every discrete GPU, or the integrated one when there is no discrete GPU
        var discrete = hw.Gpus.Where(g => !g.Integrated).ToList();
        var auto = discrete.Count > 0 ? discrete : hw.Gpus.Take(1).ToList();
        // GPUs the backend cannot see (e.g. not exposed through Vulkan) are not usable
        if (NativeBackend.IsInitialized && GpuDevices.Native().Any(d => d.GpuOrdinal >= 0))
        {
            var visible = auto.Where(g => GpuDevices.For(g) != null).ToList();
            if (visible.Count > 0) auto = visible;
        }
        return auto;
    }

    /// <summary>Memory a GPU can really offer a model, honouring the "dedicated memory only" guardrail.</summary>
    public static long GpuBudget(GpuInfo g, bool dedicatedOnly, long ramAvailable)
    {
        if (!g.Integrated)
        {
            // what the driver says is free right now (other programs may be using VRAM)
            if (NativeBackend.IsInitialized && GpuDevices.For(g) is { } dev)
            {
                long free = GpuDevices.CurrentFreeBytes(dev);
                if (free > 0) return g.VramBytes > 0 ? Math.Min(g.VramBytes, free) : free;
            }
            return g.VramBytes;
        }
        if (dedicatedOnly) return g.VramBytes;
        // shared memory is carved out of system RAM, it can never exceed what is free
        return g.VramBytes + Math.Min(g.SharedBytes, ramAvailable);
    }

    public static FitEstimate Estimate(LocalModel model, LoadOptions o, HardwareInfo? hw = null, int? gpuLayersOverride = null,
        bool? gpuBackend = null, uint? contextOverride = null, bool weightsAlreadyLoaded = false)
    {
        hw ??= Hardware.Detect();
        var settings = AppSettings.Current;
        var (info, tensors) = ReadModel(model.Path);
        var est = new FitEstimate { BlockCount = (int)info.BlockCount, Moe = info.IsMoe };
        var (ramTotal, ramAvail) = Hardware.MemoryStatus();
        est.RamTotalBytes = ramTotal; est.RamAvailableBytes = ramAvail;

        bool backendGpu = gpuBackend ?? NativeBackend.GpuPlanned;
        var devices = SelectDevices(o, hw, backendGpu);
        uint ctx = contextOverride ?? (o.ContextSize == 0 ? 4096u : o.ContextSize);
        if (info.ContextLength > 0 && ctx > info.ContextLength) ctx = (uint)info.ContextLength;
        bool dedicatedOnly = settings.LimitToDedicatedGpuMemory;

        // ---- weights per layer
        int n = (int)info.BlockCount;
        var layerBytes = new long[Math.Max(n, 0)];
        var layerHost = new long[Math.Max(n, 0)];                    // part of each layer the options keep in system memory (MoE experts...)
        var hostRules = TensorOverrides.HostRules(o);
        long outputBytes = 0, otherBytes = 0, embedBytes = 0;
        bool hasOutputTensor = false;
        var layerRx = new Regex(@"^blk\.(\d+)\.", RegexOptions.Compiled);
        foreach (var t in tensors)
        {
            var m = layerRx.Match(t.Name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int il) && il < n)
            {
                layerBytes[il] += t.Size;
                if (hostRules.Any(r => r.IsMatch(t.Name))) layerHost[il] += t.Size;
            }
            else if (t.Name == "token_embd.weight") embedBytes += t.Size;
            else if (t.Name.StartsWith("output")) { outputBytes += t.Size; if (t.Name == "output.weight") hasOutputTensor = true; }
            else otherBytes += t.Size;
        }
        long weights = layerBytes.Sum() + outputBytes + embedBytes + otherBytes;
        est.WeightsBytes = weights;
        if (weightsAlreadyLoaded)
        {
            // another instance holds these weights in memory already: this one only adds its context, cache and compute buffers
            est.SharedWeightsBytes = weights;
            est.WeightsBytes = 0; weights = 0;
            Array.Clear(layerBytes); Array.Clear(layerHost);
            outputBytes = embedBytes = otherBytes = 0;
        }

        // ---- projector
        long proj = 0;
        string? mmproj = o.MmprojPath ?? model.MmprojPath;
        if (o.Vision && !(o.Embeddings || model.IsEmbedding) && mmproj != null && File.Exists(mmproj))
            proj = (long)(new FileInfo(mmproj).Length * 1.15);
        est.ProjectorBytes = proj;
        // a draft model is loaded next to the main one (same device placement); count it like the projector
        long draftBytes = 0;
        if (!string.IsNullOrWhiteSpace(o.DraftModel) && !(o.Embeddings || model.IsEmbedding))
        {
            var dm = new ModelLibrary().Find(o.DraftModel);
            if (dm != null) draftBytes = (long)(dm.SizeBytes * 1.15) + 64L * 1024 * 1024;
        }
        est.DraftBytes = draftBytes;
        proj += draftBytes;
        long adapterBytes = 0;
        if (o.Loras is { Count: > 0 } && !(o.Embeddings || model.IsEmbedding))
        {
            var lib = new ModelLibrary();
            foreach (var l in o.Loras) if (lib.FindAdapter(l.Id) is { } ad) adapterBytes += ad.SizeBytes;
        }
        est.AdapterBytes = adapterBytes;
        proj += adapterBytes;
        // ---- KV cache per layer
        long kvHeads = info.HeadCountKv > 0 ? info.HeadCountKv : Math.Max(1, info.HeadCount);
        long kDim = info.KeyLength > 0 ? info.KeyLength : info.HeadCount > 0 ? info.EmbeddingLength / info.HeadCount : 0;
        long vDim = info.ValueLength > 0 ? info.ValueLength : kDim;
        double kBytes = ElementBytes(o.KvCacheTypeK), vBytes = ElementBytes(o.KvCacheTypeV);
        var (localFrac, window) = SlidingWindow(info);
        long ubatch = Math.Max(1, o.UBatchSize);
        double kvPerLayer;
        if (IsRecurrent(info.Architecture) || n == 0 || kDim == 0) kvPerLayer = 0;
        else
        {
            double fullCells = ctx;
            double localCells = window > 0 ? Math.Min(ctx, window + ubatch) : ctx;
            double cells = fullCells * (1 - localFrac) + localCells * localFrac;
            kvPerLayer = cells * kvHeads * (kDim * kBytes + vDim * vBytes);
        }
        est.KvCacheBytes = (long)(kvPerLayer * n);

        // ---- compute buffers
        long vocab = info.VocabSize > 0 ? info.VocabSize : 32000;
        long embd = Math.Max(info.EmbeddingLength, 1024);
        bool flash = o.FlashAttentionSetting ?? devices.Count > 0;   // automatic = on for GPU loads
        double kq = flash || o.KvCacheTypeV.ToLowerInvariant() != "f16" ? 0 : (double)Math.Max(info.HeadCount, 1) * ubatch * ctx * 4;
        double act = ubatch * embd * 4.0 * 10;
        double logits = ubatch * (double)vocab * 4;
        long compute = (long)(Math.Max(kq + act + logits, 200L * 1024 * 1024));
        est.ComputeBytes = compute;

        // ---- how many layers go where
        int requested = gpuLayersOverride ?? (o.RequestedGpuLayers(n) ?? (devices.Count == 0 ? 0 : 999));
        if (devices.Count == 0) requested = 0;
        // llama.cpp offloads the LAST k repeating layers; the output layer follows once k exceeds the block count.
        // The token embedding table always stays in system memory (tied models copy it for the output matmul).
        int Distribute(int k, out long gpu, out long cpu, out bool outGpu)
        {
            int rep = Math.Min(Math.Max(k, 0), n);
            outGpu = k > n;
            long gpuLayerWeights = 0;
            for (int i = n - rep; i < n; i++) gpuLayerWeights += layerBytes[i] - layerHost[i];
            gpu = gpuLayerWeights + (long)(rep * kvPerLayer);
            if (outGpu) gpu += outputBytes + (hasOutputTensor ? 0 : embedBytes);
            if (k > 0) gpu += compute + proj;
            cpu = weights - gpuLayerWeights - (outGpu ? outputBytes : 0)
                  + (long)((n - rep) * kvPerLayer) + (k > 0 ? 0 : compute + proj);
            return rep;
        }

        int layersWanted = requested >= 999 ? n + 1 : requested;
        est.OffloadedLayers = Distribute(layersWanted, out long gpuBytes, out long cpuBytes, out bool outOnGpu);
        est.OutputOnGpu = outOnGpu;
        est.GpuBytes = gpuBytes; est.CpuBytes = cpuBytes;

        // ---- budgets and per-device split
        if (devices.Count > 0 && gpuBytes > 0)
        {
            var budgets = devices.Select(g => GpuBudget(g, dedicatedOnly, ramAvail)).ToList();
            // same policy as the loader: keep the model on the first GPU when it fits, else fill in priority order
            var share = GpuPlanner.Shares(o, devices.Count, budgets.Select(b => b * 0.92).ToList(), gpuBytes,
                o.SplitMode.ToLowerInvariant());
            for (int i = 0; i < devices.Count; i++)
                est.Devices.Add(new DeviceFit(devices[i].Name + (devices[i].Integrated ? " (integrated)" : ""), true, (long)(gpuBytes * share[i]), budgets[i]));

            // an integrated GPU draws its "shared" part from system RAM, which then also has to hold the CPU part
            long sharedFromRam = 0;
            for (int i = 0; i < devices.Count; i++)
                if (devices[i].Integrated) sharedFromRam += Math.Max(0, est.Devices[i].NeedBytes - devices[i].VramBytes);
            cpuBytes += sharedFromRam;
        }
        est.Devices.Add(new DeviceFit("System memory", false, cpuBytes, ramAvail));

        // ---- the largest layer count that fits
        if (devices.Count > 0)
        {
            double budget = devices.Sum(g => (double)GpuBudget(g, dedicatedOnly, ramAvail)) * 0.92;
            int best = 0;
            for (int k = 1; k <= n + 1; k++)
            {
                Distribute(k, out long g2, out _, out _);
                if (g2 <= budget) best = k; else break;
            }
            est.MaxGpuLayers = best >= n + 1 ? 999 : best;
        }

        // ---- verdict
        double worst = est.Devices.Max(d => d.Ratio);
        est.Verdict = worst <= 0.92 ? "fits" : worst <= 1.0 ? "tight" : "wont-fit";

        // ---- warnings / suggestions
        if (info.ContextLength > 0 && o.ContextSize > info.ContextLength)
            est.Warnings.Add($"The requested context ({o.ContextSize}) is larger than the model's training context ({info.ContextLength}); it is capped.");
        if (devices.Any(g => g.Integrated) && !dedicatedOnly)
            est.Warnings.Add("The integrated GPU shares system RAM with the rest of the machine. Enable \"limit offload to dedicated GPU memory\" to keep it free.");
        if ((o.MoeCpuLayers != 0 || o.MoeExpertCount > 0) && !info.IsMoe)
            est.Warnings.Add("The mixture-of-experts options do not apply: this is not a mixture-of-experts model.");
        else if (info.IsMoe && o.MoeCpuLayers != 0 && est.OffloadedLayers == 0)
            est.Warnings.Add("Keeping experts in system memory only matters when layers are offloaded to a GPU.");
        if (o.KvCacheTypeV.ToLowerInvariant() != "f16" && o.FlashAttentionSetting == false)
            est.Warnings.Add("A quantized V cache needs flash attention; it will be switched on automatically.");
        if (est.Verdict != "fits" && est.KvCacheBytes > 0)
        {
            for (uint c = ctx / 2; c >= 512; c /= 2)
            {
                var e2 = Estimate(model, o, hw, gpuLayersOverride, gpuBackend, c);
                if (e2.Verdict == "fits") { est.SuggestedContext = c; break; }
            }
        }
        return est;
    }

    /// <summary>User-facing error text for a load that the guardrail refuses.</summary>
    public static string RefusalMessage(LocalModel model, FitEstimate e)
    {
        var worst = e.Devices.OrderByDescending(d => d.Ratio).First();
        string msg = $"'{model.FileName}' will probably not fit in memory: {e.Describe()}. " +
                     $"{worst.Name} would need {Hardware.FormatBytes(worst.NeedBytes)} but only {Hardware.FormatBytes(worst.BudgetBytes)} is available.";
        var tips = new List<string>();
        if (e.MaxGpuLayers >= 0 && e.OffloadedLayers > Math.Max(e.MaxGpuLayers, 0) && e.MaxGpuLayers < 999)
            tips.Add($"offload fewer layers to the GPU (about {e.MaxGpuLayers} fit)");
        if (e.SuggestedContext > 0) tips.Add($"use a smaller context (about {e.SuggestedContext} tokens fits)");
        tips.Add("pick a smaller quantization");
        tips.Add("quantize the KV cache (q8_0) with flash attention");
        return msg + " Try to " + string.Join(", ", tips) + ", or load anyway with the 'skip fit check' option.";
    }
}
