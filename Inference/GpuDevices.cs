using System.Runtime.InteropServices;
using LLMHostProject.Core;

namespace LLMHostProject.Inference;

/// <summary>A compute device as the llama.cpp / ggml backend sees it.</summary>
public record GgmlDevice(int RegistryIndex, string Name, string Description, string Type, long FreeBytes, long TotalBytes)
{
    public bool IsGpu => Type is "gpu" or "igpu";
    /// <summary>Position in llama.cpp's device list (what <c>main_gpu</c> and <c>tensor_split</c> refer to); -1 for non-GPUs.</summary>
    public int GpuOrdinal { get; init; } = -1;
}

/// <summary>
/// Enumerates the devices of the loaded native backend (Vulkan device names, free / total memory) and maps them to the
/// adapters Windows reports (DXGI), which is what the rest of the app shows to users.
/// </summary>
public static class GpuDevices
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

    private delegate nuint DevCountFn();
    private delegate IntPtr DevGetFn(nuint index);
    private delegate IntPtr DevStrFn(IntPtr dev);
    private delegate void DevMemoryFn(IntPtr dev, out nuint free, out nuint total);
    private delegate int DevTypeFn(IntPtr dev);

    private static readonly object Gate = new();
    private static List<GgmlDevice>? _native;
    private static Dictionary<int, int>? _map;

    private static T? Export<T>(string name) where T : Delegate
    {
        foreach (var module in new[] { "ggml-base.dll", "ggml.dll", "llama.dll" })
        {
            var h = GetModuleHandleW(module);
            if (h == IntPtr.Zero) continue;
            var p = GetProcAddress(h, name);
            if (p != IntPtr.Zero) return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
        return null;
    }

    /// <summary>Devices known to the native backend. Initialises the backend on first use.</summary>
    public static IReadOnlyList<GgmlDevice> Native()
    {
        lock (Gate)
        {
            if (_native != null) return _native;
            var list = new List<GgmlDevice>();
            try
            {
                NativeBackend.Initialize();
                var count = Export<DevCountFn>("ggml_backend_dev_count");
                var get = Export<DevGetFn>("ggml_backend_dev_get");
                var name = Export<DevStrFn>("ggml_backend_dev_name");
                var desc = Export<DevStrFn>("ggml_backend_dev_description");
                var mem = Export<DevMemoryFn>("ggml_backend_dev_memory");
                var type = Export<DevTypeFn>("ggml_backend_dev_type");
                if (count != null && get != null && name != null && desc != null && mem != null && type != null)
                {
                    int ordinal = 0;
                    var tmp = new List<GgmlDevice>();
                    for (nuint i = 0; i < count(); i++)
                    {
                        var dev = get(i);
                        mem(dev, out var free, out var total);
                        int t = type(dev);
                        tmp.Add(new GgmlDevice((int)i, Marshal.PtrToStringUTF8(name(dev)) ?? "", Marshal.PtrToStringUTF8(desc(dev)) ?? "",
                            t switch { 0 => "cpu", 1 => "gpu", 2 => "igpu", 3 => "accel", _ => "other" }, (long)free, (long)total));
                    }
                    // llama.cpp uses every GPU; an integrated GPU only when there is no other GPU
                    bool anyDiscrete = tmp.Any(d => d.Type == "gpu");
                    foreach (var d in tmp)
                    {
                        bool used = d.Type == "gpu" || (d.Type == "igpu" && !anyDiscrete);
                        list.Add(used ? d with { GpuOrdinal = ordinal++ } : d);
                    }
                }
            }
            catch (Exception ex)
            {
                LogBuffer.Add("backend", $"Device enumeration failed: {ex.Message}");
            }
            return _native = list;
        }
    }

    private static string Norm(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"\(tm\)|\(r\)|[^a-z0-9]", "");

    /// <summary>
    /// DXGI adapter index → the device's position in llama.cpp's GPU list. Matching is by adapter description first;
    /// identical cards (which cannot be told apart by name) are paired in enumeration order and by size.
    /// </summary>
    public static IReadOnlyDictionary<int, int> Mapping()
    {
        lock (Gate)
        {
            if (_map != null) return _map;
            var map = new Dictionary<int, int>();
            var gpus = Hardware.Detect().Gpus;
            var devs = Native().Where(d => d.GpuOrdinal >= 0).ToList();
            var usedDev = new HashSet<int>();
            foreach (var g in gpus)
            {
                // exact description, then "contains" either way
                var cands = devs.Where(d => !usedDev.Contains(d.GpuOrdinal) &&
                                            (Norm(d.Description) == Norm(g.Name) || Norm(d.Description).Contains(Norm(g.Name)) || Norm(g.Name).Contains(Norm(d.Description))))
                    .OrderBy(d => Math.Abs(d.TotalBytes - g.VramBytes)).ToList();
                if (cands.Count > 0) { map[g.Index] = cands[0].GpuOrdinal; usedDev.Add(cands[0].GpuOrdinal); }
            }
            // anything unmatched: fall back to enumeration order among the still unused devices
            foreach (var g in gpus.Where(g => !map.ContainsKey(g.Index)))
            {
                var next = devs.FirstOrDefault(d => !usedDev.Contains(d.GpuOrdinal));
                if (next != null) { map[g.Index] = next.GpuOrdinal; usedDev.Add(next.GpuOrdinal); }
            }
            return _map = map;
        }
    }

    /// <summary>Native device for a DXGI adapter, when the mapping is known.</summary>
    public static GgmlDevice? For(GpuInfo g)
    {
        if (!Mapping().TryGetValue(g.Index, out int ord)) return null;
        return Native().FirstOrDefault(d => d.GpuOrdinal == ord);
    }

    /// <summary>Free memory of a native device right now (the cached list only has the value from enumeration time).</summary>
    public static long CurrentFreeBytes(GgmlDevice d)
    {
        try
        {
            var get = Export<DevGetFn>("ggml_backend_dev_get");
            var mem = Export<DevMemoryFn>("ggml_backend_dev_memory");
            if (get == null || mem == null) return d.FreeBytes;
            mem(get((nuint)d.RegistryIndex), out var free, out _);
            return (long)free;
        }
        catch { return d.FreeBytes; }
    }

    /// <summary>Forget cached results (tests).</summary>
    public static void Reset() { lock (Gate) { _native = null; _map = null; } }
}

/// <summary>Which GPUs a model is spread over, and in which proportions (the values llama.cpp needs).</summary>
public class GpuPlan
{
    /// <summary>Positions in llama.cpp's GPU list of the devices in use, in priority order.</summary>
    public List<int> Ordinals { get; set; } = new();
    /// <summary>One entry per llama.cpp GPU; 0 for devices that must not be used. Empty = let llama.cpp decide.</summary>
    public float[] TensorSplit { get; set; } = Array.Empty<float>();
    public int MainGpu { get; set; }
    /// <summary>"layer", "row" or "none".</summary>
    public string SplitMode { get; set; } = "layer";
    public List<string> Notes { get; set; } = new();
}

public static class GpuPlanner
{
    /// <summary>
    /// Chooses devices and a tensor split. Priority order is the order of <see cref="LoadOptions.GpuDevices"/>: the
    /// model stays on the first GPU when it fits there (no cross-GPU traffic), and spills over to the next ones in order.
    /// An explicit <see cref="LoadOptions.TensorSplit"/> always wins.
    /// </summary>
    public static GpuPlan? Plan(LoadOptions o, FitEstimate? est, int gpuLayers, HardwareInfo? hw = null)
    {
        if (gpuLayers <= 0 || !NativeBackend.GpuAvailable) return null;
        hw ??= Hardware.Detect();
        var devices = FitEstimator.SelectDevices(o, hw, true);
        if (devices.Count == 0) return null;

        var plan = new GpuPlan { SplitMode = o.SplitMode.ToLowerInvariant() is "row" or "none" ? o.SplitMode.ToLowerInvariant() : "layer" };
        var native = GpuDevices.Native().Where(d => d.GpuOrdinal >= 0).ToList();
        int total = Math.Max(native.Count, hw.Gpus.Count);

        var ords = new List<int>();
        bool known = native.Count > 0;
        var usable = new List<GpuInfo>();
        foreach (var g in devices)
        {
            var d = GpuDevices.For(g);
            if (d != null) { ords.Add(d.GpuOrdinal); usable.Add(g); }
            else if (!known) { ords.Add(g.Index); usable.Add(g); }
            else plan.Notes.Add($"GPU {g.Index} ({g.Name}) is not available to the Vulkan backend and is not used.");
        }
        if (ords.Count == 0) { plan.Notes.Add("None of the selected GPUs is available to the backend; letting it choose."); return plan; }
        devices = usable;
        plan.Ordinals = ords;
        var split = new float[Math.Max(total, ords.Max() + 1)];

        var (_, ramAvail) = Hardware.MemoryStatus();
        var budgets = devices.Select(g => (double)FitEstimator.GpuBudget(g, AppSettings.Current.LimitToDedicatedGpuMemory, ramAvail) * 0.92).ToList();
        var share = Shares(o, devices.Count, budgets, est?.GpuBytes ?? 0, plan.SplitMode, plan.Notes, devices.Select(d => d.Name).ToList());
        for (int i = 0; i < ords.Count; i++) split[ords[i]] = (float)share[i];
        plan.TensorSplit = split;
        plan.MainGpu = ords[0];
        if (plan.SplitMode == "none" && devices.Count > 1) plan.SplitMode = "none";
        else if (plan.SplitMode == "none") plan.SplitMode = "layer";
        return plan;
    }

    /// <summary>
    /// Fraction of the GPU-resident model each selected device receives (sums to 1): explicit ratios if given, otherwise
    /// the first device alone when everything fits there, otherwise filled in priority order.
    /// </summary>
    public static double[] Shares(LoadOptions o, int count, IReadOnlyList<double> budgets, double need, string splitMode,
        List<string>? notes = null, IReadOnlyList<string>? names = null)
    {
        var share = new double[count];
        if (count == 0) return share;
        if (splitMode == "none" || count == 1) { share[0] = 1; return share; }

        if (o.TensorSplit is { Count: > 0 } ts)
        {
            if (ts.Count == count && ts.Sum() > 0)
            {
                double s = ts.Sum(x => (double)Math.Max(0, x));
                for (int i = 0; i < count; i++) share[i] = Math.Max(0, ts[i]) / s;
                return share;
            }
            notes?.Add($"Ignored the tensor split: it has {ts.Count} values for {count} GPUs.");
        }

        if (need <= 0 || need <= budgets[0])
        {
            share[0] = 1;
            if (need > 0) notes?.Add($"The model fits on {names?[0] ?? "the first GPU"}; the other GPU(s) stay unused.");
            return share;
        }
        double remaining = need;
        for (int i = 0; i < count && remaining > 0; i++)
        {
            double take = Math.Min(remaining, budgets[i]);
            share[i] = take;
            remaining -= take;
        }
        if (remaining > 0)
        {
            double sum = Math.Max(1, budgets.Sum());
            for (int i = 0; i < count; i++) share[i] += remaining * budgets[i] / sum;
        }
        for (int i = 0; i < count; i++) share[i] /= need;
        notes?.Add("Split across GPUs: " + string.Join(", ", Enumerable.Range(0, count).Select(i => $"{names?[i] ?? "GPU " + i} {share[i]:P0}")));
        return share;
    }
}
