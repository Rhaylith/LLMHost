using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace LLMHostProject.Core;

/// <summary>One GPU as seen right now. Values are null when Windows does not provide the counter for that adapter.</summary>
public record GpuLive(int Index, string Name, double? Utilization, long? DedicatedUsedBytes, long DedicatedTotalBytes,
    long? SharedUsedBytes, long? ProcessDedicatedBytes, long? ProcessSharedBytes, double? TemperatureC);

/// <summary>A reading of the machine: processor, memory, this process, and every GPU.</summary>
public record LiveSnapshot(DateTime Time, double? CpuPercent, long RamTotalBytes, long RamAvailableBytes,
    long ProcessWorkingSetBytes, long ProcessPrivateBytes, IReadOnlyList<GpuLive> Gpus, IReadOnlyList<string> Notes);

/// <summary>A parsed Windows "GPU Engine" / "GPU Adapter Memory" / "GPU Process Memory" counter instance name.</summary>
public record GpuCounterName(long Luid, int? Pid, int Phys, int? Engine, string? EngineType)
{
    private static readonly Regex Rx = new(
        @"^(?:pid_(?<pid>\d+)_)?luid_0x(?<hi>[0-9A-Fa-f]{8})_0x(?<lo>[0-9A-Fa-f]{8})_phys_(?<phys>\d+)(?:_eng_(?<eng>\d+)_engtype_(?<type>.+))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses names such as <c>pid_1234_luid_0x00000000_0x0000F5A3_phys_0_eng_0_engtype_3D</c>. The LUID is the one DXGI reports for the
    /// adapter (high part in the upper 32 bits), which is how a counter is matched to a GPU.
    /// </summary>
    public static GpuCounterName? Parse(string? name)
    {
        if (name == null) return null;
        var m = Rx.Match(name.Trim());
        if (!m.Success) return null;
        long hi = int.Parse(m.Groups["hi"].Value, System.Globalization.NumberStyles.HexNumber);
        long lo = uint.Parse(m.Groups["lo"].Value, System.Globalization.NumberStyles.HexNumber);
        return new GpuCounterName((hi << 32) | lo,
            m.Groups["pid"].Success ? int.Parse(m.Groups["pid"].Value) : null,
            int.Parse(m.Groups["phys"].Value),
            m.Groups["eng"].Success ? int.Parse(m.Groups["eng"].Value) : null,
            m.Groups["type"].Success ? m.Groups["type"].Value : null);
    }

    /// <summary>
    /// Utilisation of each adapter from engine counters: processes are added up per engine (3D, compute, copy, ...) and the busiest
    /// engine counts, like the GPU graph of the task manager. Capped at 100.
    /// </summary>
    public static Dictionary<long, double> AdapterUtilization(IEnumerable<(string Name, double Value)> engines)
    {
        var perEngine = new Dictionary<(long Luid, int Engine, string Type), double>();
        foreach (var (name, value) in engines)
        {
            var p = Parse(name);
            if (p?.Engine == null) continue;
            var key = (p.Luid, p.Engine.Value, p.EngineType ?? "");
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }
        return perEngine.GroupBy(kv => kv.Key.Luid).ToDictionary(g => g.Key, g => Math.Min(100.0, g.Max(kv => kv.Value)));
    }
}

/// <summary>
/// Reads processor, memory and GPU load from Windows performance counters. Sampling runs once a second, and only while somebody
/// listens (a stream is open); a plain request takes a fresh reading when the last one is older than a second.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    public static HardwareMonitor Shared { get; } = new();

    private readonly object _lock = new();
    private PerformanceCounter? _cpu;
    private bool _cpuTried;
    private LiveSnapshot? _last;
    private int _listeners;
    private CancellationTokenSource? _loop;
    private readonly List<string> _notes = new();

    /// <summary>Raised on the sampling thread for every new reading while listeners are subscribed.</summary>
    public event Action<LiveSnapshot>? Sampled;

    public int Listeners => Volatile.Read(ref _listeners);

    /// <summary>Starts sampling every second until the returned object is disposed (sampling stops when the last listener leaves).</summary>
    public IDisposable Subscribe()
    {
        lock (_lock)
        {
            if (_listeners++ == 0)
            {
                _loop = new CancellationTokenSource();
                var token = _loop.Token;
                _ = Task.Run(() => Loop(token));
            }
        }
        return new Subscription(this);
    }

    private sealed class Subscription(HardwareMonitor owner) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (owner._lock)
            {
                if (--owner._listeners == 0) { owner._loop?.Cancel(); owner._loop = null; }
            }
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { var s = Sample(); Sampled?.Invoke(s); } catch { /* a bad reading must not end the loop */ }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>The newest reading; a new one is taken when the cached one is older than <paramref name="maxAge"/>.</summary>
    public LiveSnapshot Latest(TimeSpan? maxAge = null)
    {
        lock (_lock)
        {
            if (_last != null && DateTime.UtcNow - _last.Time < (maxAge ?? TimeSpan.FromMilliseconds(900))) return _last;
        }
        return Sample();
    }

    public LiveSnapshot Sample()
    {
        lock (_lock)
        {
            var notes = new List<string>();
            double? cpu = ReadCpu(notes);
            var (total, avail) = Hardware.MemoryStatus();
            long ws = 0, priv = 0;
            try { using var p = Process.GetCurrentProcess(); ws = p.WorkingSet64; priv = p.PrivateMemorySize64; } catch { }
            var gpus = ReadGpus(notes);
            _last = new LiveSnapshot(DateTime.UtcNow, cpu, total, avail, ws, priv, gpus, notes);
            return _last;
        }
    }

    private double? ReadCpu(List<string> notes)
    {
        if (!_cpuTried)
        {
            _cpuTried = true;
            foreach (var (cat, counter) in new[] { ("Processor Information", "% Processor Utility"), ("Processor", "% Processor Time") })
            {
                try { _cpu = new PerformanceCounter(cat, counter, "_Total", readOnly: true); _cpu.NextValue(); break; }
                catch { _cpu?.Dispose(); _cpu = null; }
            }
        }
        if (_cpu == null) { notes.Add("Processor load is not available (performance counters could not be read)."); return null; }
        try { return Math.Round(Math.Clamp(_cpu.NextValue(), 0f, 100f), 1); }
        catch { notes.Add("Processor load could not be read."); return null; }
    }

    private sealed record CounterRow(string Name, long? A, long? B);

    /// <summary>Reads (name, column a, column b) of every row of a WMI performance-counter class.</summary>
    private static List<CounterRow> Query(string wql, string colA, string? colB = null)
    {
        var list = new List<CounterRow>();
        using var searcher = new ManagementObjectSearcher(wql);
        foreach (ManagementBaseObject o in searcher.Get())
        {
            using (o)
            {
                if (o["Name"] is string name) list.Add(new CounterRow(name, AsLong(o[colA]), colB == null ? null : AsLong(o[colB])));
            }
        }
        return list;
    }

    private static long? AsLong(object? v) => v switch { null => null, ulong u => (long)u, long l => l, uint i => i, _ => long.TryParse(v.ToString(), out var n) ? n : null };

    private List<GpuLive> ReadGpus(List<string> notes)
    {
        var hw = Hardware.Detect();
        if (hw.Gpus.Count == 0) return new List<GpuLive>();
        Dictionary<long, double> util = new();
        Dictionary<long, (long? Dedicated, long? Shared)> mem = new();
        Dictionary<long, (long? Dedicated, long? Shared)> proc = new();
        try
        {
            var engines = Query("select Name, UtilizationPercentage from Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine", "UtilizationPercentage");
            util = GpuCounterName.AdapterUtilization(engines.Where(e => e.A.HasValue).Select(e => (e.Name, (double)e.A!.Value)));
        }
        catch { notes.Add("GPU utilisation is not available on this system."); }
        try
        {
            foreach (var row in Query("select Name, DedicatedUsage, SharedUsage from Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory", "DedicatedUsage", "SharedUsage"))
                if (GpuCounterName.Parse(row.Name) is { } p) mem[p.Luid] = (row.A, row.B);
        }
        catch { notes.Add("GPU memory use is not available on this system."); }
        try
        {
            int pid = Environment.ProcessId;
            foreach (var row in Query($"select Name, DedicatedUsage, SharedUsage from Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory where Name like 'pid_{pid}_%'", "DedicatedUsage", "SharedUsage"))
                if (GpuCounterName.Parse(row.Name) is { } p) proc[p.Luid] = (row.A, row.B);
        }
        catch { /* per-process figures are a bonus */ }

        return hw.Gpus.Select(g => new GpuLive(g.Index, g.Name,
            g.Luid != 0 && util.TryGetValue(g.Luid, out var u) ? Math.Round(u, 1) : (g.Luid != 0 && util.Count > 0 ? 0 : null),
            g.Luid != 0 && mem.TryGetValue(g.Luid, out var m) ? m.Dedicated : null, g.VramBytes,
            g.Luid != 0 && mem.TryGetValue(g.Luid, out var m2) ? m2.Shared : null,
            g.Luid != 0 && proc.TryGetValue(g.Luid, out var pr) ? pr.Dedicated : null,
            g.Luid != 0 && proc.TryGetValue(g.Luid, out var pr2) ? pr2.Shared : null,
            null)).ToList();                       // temperature: Windows offers no vendor-neutral counter, so it is reported as unavailable
    }

    public void Dispose()
    {
        _loop?.Cancel();
        _cpu?.Dispose();
    }
}
