namespace LLMHostProject.Inference;

/// <summary>
/// Which loaded model occupies how much of which GPU. The driver's "free memory" figure lags behind a load and knows nothing
/// of what a load is about to claim, so the memory estimate for a new model also subtracts what the models that are already
/// loaded were estimated to need on each GPU. GPUs are identified by the adapter index of <see cref="Core.GpuInfo"/>.
/// </summary>
public static class GpuLedger
{
    public record Entry(string Model, IReadOnlyDictionary<int, long> BytesByGpu);

    private static readonly object Gate = new();
    private static readonly Dictionary<object, Entry> Entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>Records what a loaded model needs on each GPU (GPU index → bytes). Models without GPU memory are not recorded.</summary>
    public static void Register(object owner, string model, IReadOnlyDictionary<int, long> bytesByGpu)
    {
        var used = bytesByGpu.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        lock (Gate)
        {
            if (used.Count == 0) Entries.Remove(owner);
            else Entries[owner] = new Entry(model, used);
        }
    }

    public static void Release(object owner) { lock (Gate) Entries.Remove(owner); }

    /// <summary>What models other than <paramref name="except"/> occupy on a GPU.</summary>
    public static long Reserved(int gpuIndex, object? except = null)
    {
        lock (Gate) return Entries.Where(e => !ReferenceEquals(e.Key, except)).Sum(e => e.Value.BytesByGpu.GetValueOrDefault(gpuIndex));
    }

    /// <summary>The models that occupy part of a GPU, largest first.</summary>
    public static List<(string Model, long Bytes)> Users(int gpuIndex, object? except = null)
    {
        lock (Gate)
            return Entries.Where(e => !ReferenceEquals(e.Key, except) && e.Value.BytesByGpu.GetValueOrDefault(gpuIndex) > 0)
                .Select(e => (e.Value.Model, e.Value.BytesByGpu[gpuIndex])).OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>Every GPU that has at least one model on it.</summary>
    public static List<(int Gpu, List<(string Model, long Bytes)> Users)> Snapshot()
    {
        lock (Gate)
            return Entries.Values.SelectMany(e => e.BytesByGpu.Keys).Distinct().OrderBy(i => i)
                .Select(i => (i, Entries.Values.Where(e => e.BytesByGpu.GetValueOrDefault(i) > 0)
                    .Select(e => (e.Model, e.BytesByGpu[i])).OrderByDescending(x => x.Item2).ToList())).ToList();
    }

    /// <summary>Forget everything (tests).</summary>
    public static void Clear() { lock (Gate) Entries.Clear(); }
}
