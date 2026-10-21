using System.Runtime.InteropServices;
using LLMHostProject.Core;
using LLama.Native;

namespace LLMHostProject.Inference;

/// <summary>
/// Selects and configures the native llama.cpp backend. This must happen before the first model is loaded and
/// is process-wide. The only GPU backend is Vulkan (works on AMD, NVIDIA and Intel GPUs); otherwise CPU.
/// </summary>
public static class NativeBackend
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>"vulkan" or "cpu".</summary>
    public static string Active { get; private set; } = "cpu";
    public static bool GpuAvailable => Active != "cpu";
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Whether a GPU backend is (or, before the first load, will be) used: lets planning code decide about
    /// offloading without having to initialise the native library.
    /// </summary>
    public static bool GpuPlanned
    {
        get
        {
            if (_initialized) return Active != "cpu";
            string backend = AppSettings.Current.Backend.ToLowerInvariant();
            return backend == "vulkan" || (backend == "auto" && Hardware.Detect().Gpus.Count > 0);
        }
    }
    public static string SystemInfo { get; private set; } = "";
    /// <summary>Id of the runtime build in use ("bundled:vulkan", "user:name"...); empty before initialisation.</summary>
    public static string ActiveRuntimeId { get; private set; } = "";

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            var s = AppSettings.Current;
            var cfg = NativeLibraryConfig.All;

            cfg.WithLogCallback((level, message) =>
            {
                if (level == LLamaLogLevel.Debug && !s.VerboseNativeLog) return;
                LogBuffer.Add("llama", message);
            });

            string backend = s.Backend.ToLowerInvariant();
            bool hasGpu = Hardware.Detect().Gpus.Count > 0;
            if (backend == "auto") backend = hasGpu ? "vulkan" : "cpu";

            // an explicit runtime choice (runtime manager) overrides the backend setting
            RuntimeInfo? chosen = null;
            if (!s.Runtime.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                chosen = RuntimeManager.Find(s.Runtime);
                if (chosen == null || !chosen.Compatible)
                {
                    LogBuffer.Add("backend", $"Runtime '{s.Runtime}' is not usable ({chosen?.Reason ?? "not installed"}); using the automatic choice.");
                    chosen = null;
                }
            }

            if (chosen is { Source: "user" })
            {
                cfg.WithLibrary(chosen.LlamaPath, File.Exists(chosen.MtmdPath) ? chosen.MtmdPath : null);
                Active = chosen.Kind;
                ActiveRuntimeId = chosen.Id;
            }
            else
            {
                if (chosen != null) backend = chosen.Kind == "vulkan" ? "vulkan" : "cpu";
                if (backend == "vulkan")
                {
                    cfg.WithCuda(false).WithVulkan(true).WithAutoFallback(true);
                    Active = "vulkan";
                }
                else
                {
                    cfg.WithCuda(false).WithVulkan(false).WithAutoFallback(true);
                    if (chosen != null)
                        cfg.WithAvx(chosen.Variant switch
                        {
                            "avx512" => AvxLevel.Avx512, "avx2" => AvxLevel.Avx2, "avx" => AvxLevel.Avx, _ => AvxLevel.None,
                        });
                    Active = "cpu";
                }
                ActiveRuntimeId = chosen?.Id ?? (Active == "vulkan" ? "bundled:vulkan" : "bundled:auto-cpu");
            }

            try
            {
                NativeApi.llama_empty_call();   // forces the native library to load
                SystemInfo = Marshal.PtrToStringUTF8(NativeApi.llama_print_system_info()) ?? "";
            }
            catch (Exception ex)
            {
                LogBuffer.Add("backend", $"Failed to load '{Active}' backend: {ex.Message}");
                if (Active != "cpu") throw;
            }
            _initialized = true;
            LogBuffer.Add("backend", $"Native backend: {Active}. {SystemInfo}");
        }
    }
}
