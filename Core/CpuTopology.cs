using System.Runtime.InteropServices;

namespace LLMHostProject.Core;

/// <summary>One logical processor as Windows describes it (the "CPU set" view that also works across processor groups).</summary>
/// <param name="Global">Position in the machine-wide numbering used by the CPU lists of this app (group by group).</param>
/// <param name="SetId">Windows CPU set id, what the affinity APIs take.</param>
public record LogicalCpu(int Global, uint SetId, int Group, int Index, int Core, int Node, int Efficiency);

/// <summary>NUMA node with the global CPU numbers that belong to it.</summary>
public record NumaNodeInfo(int Node, IReadOnlyList<int> Cpus);

/// <summary>
/// Logical processors, physical cores, processor groups and NUMA nodes. A machine with more than 64 logical processors is split into
/// processor groups by Windows; the global numbering here counts through all of them (0 .. N-1).
/// </summary>
public sealed class CpuTopology
{
    public IReadOnlyList<LogicalCpu> Cpus { get; }
    public int LogicalCount => Cpus.Count;
    /// <summary>Physical cores (hyper-threads / SMT siblings count once).</summary>
    public int PhysicalCores { get; }
    public int Groups { get; }
    public IReadOnlyList<NumaNodeInfo> Nodes { get; }

    public CpuTopology(IReadOnlyList<LogicalCpu> cpus)
    {
        Cpus = cpus;
        PhysicalCores = Math.Max(1, cpus.Select(c => (c.Group, c.Core)).Distinct().Count());
        Groups = Math.Max(1, cpus.Select(c => c.Group).Distinct().Count());
        Nodes = cpus.GroupBy(c => c.Node).OrderBy(g => g.Key).Select(g => new NumaNodeInfo(g.Key, g.Select(c => c.Global).OrderBy(x => x).ToList())).ToList();
    }

    private static CpuTopology? _cached;

    /// <summary>The topology of this machine (read once). When Windows cannot tell, a plain list of <c>ProcessorCount</c> processors.</summary>
    public static CpuTopology Detect() => _cached ??= Read() ?? Fallback();

    public static CpuTopology Fallback(int count = 0)
    {
        count = count > 0 ? count : Math.Max(1, Environment.ProcessorCount);
        return new CpuTopology(Enumerable.Range(0, count).Select(i => new LogicalCpu(i, 0, 0, i, i, 0, 0)).ToList());
    }

    // ---- Windows CPU sets (Windows 10+)

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    private static CpuTopology? Read()
    {
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint needed, IntPtr.Zero, 0);
            if (needed == 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, needed, out uint got, IntPtr.Zero, 0)) return null;
                return Parse(buffer, (int)got);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return null; }
    }

    /// <summary>Decodes the SYSTEM_CPU_SET_INFORMATION records (32 bytes each for CPU sets).</summary>
    internal static CpuTopology? Parse(IntPtr buffer, int length)
    {
        var raw = new List<(uint Id, int Group, int Index, int Core, int Node, int Efficiency)>();
        int offset = 0;
        while (offset + 8 <= length)
        {
            int size = Marshal.ReadInt32(buffer, offset);
            int type = Marshal.ReadInt32(buffer, offset + 4);
            if (size <= 0) break;
            if (type == 0 && offset + 20 <= length)                      // CpuSetInformation
            {
                uint id = (uint)Marshal.ReadInt32(buffer, offset + 8);
                int group = Marshal.ReadInt16(buffer, offset + 12);
                int index = Marshal.ReadByte(buffer, offset + 14);
                int core = Marshal.ReadByte(buffer, offset + 15);
                int node = Marshal.ReadByte(buffer, offset + 17);
                int eff = Marshal.ReadByte(buffer, offset + 18);
                raw.Add((id, group, index, core, node, eff));
            }
            offset += size;
        }
        if (raw.Count == 0) return null;
        var ordered = raw.OrderBy(r => r.Group).ThenBy(r => r.Index).ToList();
        return new CpuTopology(ordered.Select((r, i) => new LogicalCpu(i, r.Id, r.Group, r.Index, r.Core, r.Node, r.Efficiency)).ToList());
    }
}

/// <summary>
/// Which processors a thread may use, written the way people say it:
/// <c>0-7,16-23</c> (global numbers), <c>node:1</c> (one NUMA node), <c>physical</c> (one thread per physical core, no SMT siblings),
/// <c>all</c>. Parts can be combined with commas.
/// </summary>
public static class CpuAffinity
{
    /// <summary>The processors named by <paramref name="spec"/>; null for "no restriction" (empty or "all"). Throws <see cref="ArgumentException"/> for a bad spec.</summary>
    public static IReadOnlyList<LogicalCpu>? Parse(string? spec, CpuTopology topology)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var chosen = new SortedDictionary<int, LogicalCpu>();
        foreach (var rawPart in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = rawPart.ToLowerInvariant();
            if (part == "all") return null;
            if (part == "physical")
            {
                // the first logical processor of every physical core
                foreach (var g in topology.Cpus.GroupBy(c => (c.Group, c.Core))) { var first = g.OrderBy(c => c.Global).First(); chosen[first.Global] = first; }
            }
            else if (part.StartsWith("node:"))
            {
                if (!int.TryParse(part[5..], out int node) || topology.Nodes.All(n => n.Node != node))
                    throw new ArgumentException($"There is no NUMA node '{part[5..]}' (nodes: {string.Join(", ", topology.Nodes.Select(n => n.Node))}).");
                foreach (var c in topology.Cpus.Where(c => c.Node == node)) chosen[c.Global] = c;
            }
            else
            {
                var ends = part.Split('-');
                int from = 0, to = 0;
                bool ok = ends.Length is 1 or 2 && int.TryParse(ends[0], out from) && int.TryParse(ends[^1], out to) && from >= 0 && to >= from;
                if (!ok) throw new ArgumentException($"'{rawPart}' is not a processor number, a range such as 0-7, 'node:N' or 'physical'.");
                if (to >= topology.LogicalCount) throw new ArgumentException($"Processor {to} does not exist; this machine has {topology.LogicalCount} (0-{topology.LogicalCount - 1}).");
                for (int i = from; i <= to; i++) chosen[i] = topology.Cpus[i];
            }
        }
        return chosen.Values.ToList();
    }

    /// <summary>Text for a list of processors, collapsing runs: <c>0-3,8,10-11</c>.</summary>
    public static string Format(IEnumerable<int> cpus)
    {
        var list = cpus.OrderBy(x => x).Distinct().ToList();
        var parts = new List<string>();
        for (int i = 0; i < list.Count;)
        {
            int j = i;
            while (j + 1 < list.Count && list[j + 1] == list[j] + 1) j++;
            parts.Add(j > i ? $"{list[i]}-{list[j]}" : list[i].ToString());
            i = j + 1;
        }
        return string.Join(",", parts);
    }
}
