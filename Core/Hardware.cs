using System.Management;
using System.Runtime.InteropServices;

namespace LLMHostProject.Core;

public record GpuInfo(int Index, string Name, string Vendor, long VramBytes, bool Integrated, string Driver, long SharedBytes = 0)
{
    /// <summary>Memory the GPU can use for a model: dedicated VRAM, plus shared system memory for integrated GPUs.</summary>
    public long UsableBytes => VramBytes + (Integrated ? SharedBytes : 0);
}

/// <summary>An NPU (neural processing unit). Detected and reported only; no inference backend targets it yet.</summary>
public record NpuInfo(string Name, string Vendor, string Architecture, string Driver, string Status);

public record HardwareInfo(string Cpu, int LogicalCores, long TotalRamBytes, IReadOnlyList<GpuInfo> Gpus, IReadOnlyList<NpuInfo>? NpuList = null)
{
    public IReadOnlyList<NpuInfo> Npus => NpuList ?? Array.Empty<NpuInfo>();

    /// <summary>Largest discrete GPU (or any GPU if none is discrete).</summary>
    public GpuInfo? BestGpu => Gpus.Where(g => !g.Integrated).OrderByDescending(g => g.UsableBytes).FirstOrDefault()
                               ?? Gpus.OrderByDescending(g => g.UsableBytes).FirstOrDefault();
}

public static class Hardware
{
    private static HardwareInfo? _cached;

    public static HardwareInfo Detect()
    {
        if (_cached != null) return _cached;
        string cpu = "Unknown CPU";
        var gpus = new List<GpuInfo>();
        try
        {
            using (var s = new ManagementObjectSearcher("select Name from Win32_Processor"))
                foreach (var o in s.Get()) { cpu = o["Name"]?.ToString()?.Trim() ?? cpu; break; }

            var dxgi = EnumerateDxgi();
            if (dxgi.Count > 0)
            {
                // DXGI reports the real (64-bit) dedicated VRAM, unlike WMI's 4 GB capped AdapterRAM
                var drivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var g = new ManagementObjectSearcher("select Name, DriverVersion from Win32_VideoController"))
                    foreach (var o in g.Get()) drivers[o["Name"]?.ToString() ?? ""] = o["DriverVersion"]?.ToString() ?? "";
                int idx = 0;
                foreach (var (name, vendorId, vram, shared) in dxgi)
                {
                    string vendor = vendorId switch { 0x1002 => "AMD", 0x10DE => "NVIDIA", 0x8086 => "Intel", _ => Vendor(name) };
                    bool integrated = IsIntegrated(name, vram);
                    gpus.Add(new GpuInfo(idx++, name, vendor, vram, integrated, drivers.GetValueOrDefault(name, ""), shared));
                }
            }
            else
            {
                int idx = 0;
                using var g = new ManagementObjectSearcher("select Name, AdapterRAM, DriverVersion from Win32_VideoController");
                foreach (var o in g.Get())
                {
                    string name = o["Name"]?.ToString() ?? "GPU";
                    if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                    long bytes = o["AdapterRAM"] is uint u ? u : 0;
                    gpus.Add(new GpuInfo(idx++, name, Vendor(name), bytes, IsIntegrated(name, bytes), o["DriverVersion"]?.ToString() ?? ""));
                }
            }
        }
        catch { /* WMI unavailable: report what we have */ }

        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return _cached = new HardwareInfo(cpu, Environment.ProcessorCount, ram, gpus, DetectNpus());
    }

    /// <summary>
    /// AMD NPUs (XDNA / Ryzen AI) enumerate as PnP devices of class ComputeAccelerator with PCI vendor 1022.
    /// </summary>
    private static List<NpuInfo> DetectNpus()
    {
        var npus = new List<NpuInfo>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "select Name, Manufacturer, DeviceID, Status from Win32_PnPEntity where PNPClass = 'ComputeAccelerator'");
            foreach (ManagementObject o in s.Get())
            {
                string id = o["DeviceID"]?.ToString() ?? "";
                string manufacturer = o["Manufacturer"]?.ToString() ?? "";
                bool amd = id.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase)
                           || manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase);
                if (!amd) continue;

                string arch = id switch
                {
                    _ when id.Contains("DEV_1502", StringComparison.OrdinalIgnoreCase) => "XDNA",     // Phoenix / Hawk Point
                    _ when id.Contains("DEV_17F0", StringComparison.OrdinalIgnoreCase) => "XDNA 2",   // Strix Point / Strix Halo / Krackan
                    _ => "XDNA",
                };
                string driver = "";
                try
                {
                    string escaped = id.Replace("\\", "\\\\");
                    using var d = new ManagementObjectSearcher($"select DriverVersion from Win32_PnPSignedDriver where DeviceID = '{escaped}'");
                    foreach (var x in d.Get()) { driver = x["DriverVersion"]?.ToString() ?? ""; break; }
                }
                catch { }
                npus.Add(new NpuInfo(o["Name"]?.ToString()?.Trim() ?? "NPU", "AMD", arch, driver, o["Status"]?.ToString() ?? ""));
            }
        }
        catch { /* WMI unavailable */ }
        return npus;
    }

    /// <summary>
    /// Integrated GPUs share system RAM. APUs often reserve more than 2 GB of dedicated memory in the BIOS,
    /// so a small-VRAM check alone misses them; the adapter name ("Radeon(TM) 8060S Graphics") is the tell.
    /// </summary>
    private static bool IsIntegrated(string name, long dedicatedBytes)
    {
        if (dedicatedBytes <= 2L * 1024 * 1024 * 1024) return true;
        if (!name.Contains("Graphics", StringComparison.OrdinalIgnoreCase)) return false;   // RX / RTX / GTX / Pro cards
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"Arc\W*(TM)?\W*[AB]\d")) return false;   // discrete Intel Arc
        return name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.Contains("Intel", StringComparison.OrdinalIgnoreCase);
    }

    private static string Vendor(string name)
    {
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return "Unknown";
    }

    // ---- DXGI adapter enumeration (dedicated VRAM as a 64-bit value) ----

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 adapter);
    }

    private static List<(string Name, uint VendorId, long Vram, long Shared)> EnumerateDxgi()
    {
        var list = new List<(string, uint, long, long)>();
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
            {
                if (adapter.GetDesc1(out var d) == 0 && (d.Flags & 2) == 0)   // skip DXGI_ADAPTER_FLAG_SOFTWARE
                    list.Add((d.Description, d.VendorId, (long)d.DedicatedVideoMemory.ToUInt64(), (long)d.SharedSystemMemory.ToUInt64()));
                Marshal.ReleaseComObject(adapter);
            }
            Marshal.ReleaseComObject(factory);
        }
        catch { }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Physical memory right now: total and currently available (free + reclaimable), in bytes.</summary>
    public static (long Total, long Available) MemoryStatus()
    {
        try
        {
            var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref s)) return ((long)s.TotalPhys, (long)s.AvailPhys);
        }
        catch { }
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (total, total / 2);
    }

    /// <summary>Default inference thread count: 75% of the logical cores (at least 1).</summary>
    public static int DefaultThreads => Math.Max(1, (int)Math.Round(Environment.ProcessorCount * 0.75));

    /// <summary>Resolves a user supplied thread count where 0 (or less) means "use the default".</summary>
    public static int EffectiveThreads(int requested) => requested > 0 ? requested : DefaultThreads;

    public static string FormatBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
