using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LLMHostProject.Core;

namespace LLMHostProject.Inference;

/// <summary>
/// Pins the compute threads of llama.cpp to chosen processors and gives them a priority. Those threads are created inside the native
/// libraries (OpenMP / ggml), so they are found by where they start: a thread whose start address lies in vcomp / libomp / ggml / llama
/// belongs to the engine. Windows CPU sets are used for pinning, which works across processor groups (machines with more than 64
/// logical processors). The policy is process wide: the model loaded last decides, and unloading it restores the default.
/// </summary>
public static class NativeThreadPolicy
{
    private static readonly object Gate = new();
    private static object? _owner;
    private static uint[]? _cpuSetIds;
    private static int _priority;                                         // Win32 thread priority offset: 0 normal, 1 above, -1 below
    private static readonly HashSet<int> Handled = new();
    private static System.Threading.Timer? _timer;
    private static List<(long Start, long End)>? _moduleRanges;

    private static readonly Regex NativeModule = new(@"^(vcomp|libomp|libgomp|ggml|llama|mtmd)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Priority offset for the setting names normal | high | low.</summary>
    public static int PriorityOffset(string? name) => (name ?? "normal").Trim().ToLowerInvariant() switch { "high" => 1, "low" => -1, _ => 0 };

    /// <summary>Whether a load with these options needs the pinner at all.</summary>
    public static bool Wanted(LoadOptions o) => !string.IsNullOrWhiteSpace(o.CpuAffinity) || PriorityOffset(o.ThreadPriority) != 0;

    /// <summary>Makes <paramref name="owner"/>'s options the policy (null processors = no pinning). Threads found from now on, and the ones already running, get it.</summary>
    public static void Set(object owner, IReadOnlyList<LogicalCpu>? cpus, string? priority)
    {
        lock (Gate)
        {
            Reset();
            _owner = owner;
            _cpuSetIds = cpus is { Count: > 0 } ? cpus.Select(c => c.SetId).ToArray() : null;
            _priority = PriorityOffset(priority);
            if (_cpuSetIds == null && _priority == 0) { _owner = null; return; }
            _timer ??= new System.Threading.Timer(_ => { try { ScanNow(); } catch { } }, null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(0, 1000);                                         // threads appear when the first request is computed
        }
    }

    /// <summary>Ends the policy of <paramref name="owner"/> (when it is still the current one) and releases the threads.</summary>
    public static void Release(object owner)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            Reset();
            _owner = null; _cpuSetIds = null; _priority = 0;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private static void Reset()
    {
        // give threads that were pinned before their normal scheduling back
        foreach (var tid in Handled.ToList())
        {
            var h = OpenThread(ThreadSetInformation | ThreadQueryInformation, false, (uint)tid);
            if (h == IntPtr.Zero) continue;
            try { SetThreadSelectedCpuSets(h, null, 0); SetThreadPriority(h, 0); } finally { CloseHandle(h); }
        }
        Handled.Clear();
    }

    /// <summary>The engine threads that exist right now.</summary>
    public static IReadOnlyList<int> FindNativeThreads()
    {
        var ranges = NativeRanges();
        var found = new List<int>();
        foreach (ProcessThread t in Process.GetCurrentProcess().Threads)
        {
            var h = OpenThread(ThreadQueryInformation, false, (uint)t.Id);
            if (h == IntPtr.Zero) continue;
            try
            {
                if (NtQueryInformationThread(h, ThreadQuerySetWin32StartAddress, out long start, sizeof(long), out _) == 0
                    && ranges.Any(r => start >= r.Start && start < r.End))
                    found.Add(t.Id);
            }
            finally { CloseHandle(h); }
        }
        return found;
    }

    /// <summary>Applies the policy to engine threads that have not been handled yet. Returns how many were handled now.</summary>
    public static int ScanNow()
    {
        lock (Gate)
        {
            if (_owner == null) return 0;
            int applied = 0;
            foreach (var tid in FindNativeThreads())
            {
                if (!Handled.Add(tid)) continue;
                var h = OpenThread(ThreadSetInformation | ThreadQueryInformation, false, (uint)tid);
                if (h == IntPtr.Zero) { Handled.Remove(tid); continue; }
                try
                {
                    if (_cpuSetIds != null) SetThreadSelectedCpuSets(h, _cpuSetIds, (uint)_cpuSetIds.Length);
                    if (_priority != 0) SetThreadPriority(h, _priority);
                    applied++;
                }
                finally { CloseHandle(h); }
            }
            // threads that ended: forget them so a reused id is handled again
            var alive = new HashSet<int>(Process.GetCurrentProcess().Threads.Cast<ProcessThread>().Select(t => t.Id));
            Handled.RemoveWhere(id => !alive.Contains(id));
            return applied;
        }
    }

    /// <summary>The CPU set ids a thread is restricted to (empty = no restriction). For tests and diagnostics.</summary>
    public static uint[] SelectedCpuSets(int threadId)
    {
        var h = OpenThread(ThreadQueryInformation, false, (uint)threadId);
        if (h == IntPtr.Zero) return Array.Empty<uint>();
        try
        {
            GetThreadSelectedCpuSets(h, null, 0, out uint needed);
            if (needed == 0) return Array.Empty<uint>();
            var ids = new uint[needed];
            return GetThreadSelectedCpuSets(h, ids, needed, out _) ? ids : Array.Empty<uint>();
        }
        finally { CloseHandle(h); }
    }

    private static List<(long Start, long End)> NativeRanges()
    {
        // the libraries stay loaded; read the list again only when it was empty or a new engine library appeared
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(m => NativeModule.IsMatch(m.ModuleName ?? ""))
            .Select(m => ((long)m.BaseAddress, (long)m.BaseAddress + m.ModuleMemorySize)).ToList();
        _moduleRanges = modules;
        return modules;
    }

    private const uint ThreadSetInformation = 0x0020, ThreadQueryInformation = 0x0040;
    private const int ThreadQuerySetWin32StartAddress = 9;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadSelectedCpuSets(IntPtr thread, uint[]? cpuSetIds, uint count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadSelectedCpuSets(IntPtr thread, uint[]? cpuSetIds, uint count, out uint required);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, out long info, int length, out int returned);
}
