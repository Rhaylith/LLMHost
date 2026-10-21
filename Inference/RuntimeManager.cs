using System.IO.Compression;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using LLMHostProject.Core;
using LLMHostProject.Models;

namespace LLMHostProject.Inference;

/// <summary>One llama.cpp build the app can run on: bundled with the app or installed by the user.</summary>
public class RuntimeInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"cpu" or "vulkan".</summary>
    public string Kind { get; set; } = "cpu";
    /// <summary>noavx / avx / avx2 / avx512 for CPU builds, "vulkan" otherwise.</summary>
    public string Variant { get; set; } = "";
    /// <summary>"bundled" (ships with the app) or "user" (installed under ~/.llmhost/runtimes).</summary>
    public string Source { get; set; } = "bundled";
    public string Directory { get; set; } = "";
    public string LlamaPath => System.IO.Path.Combine(Directory, "llama.dll");
    public string MtmdPath => System.IO.Path.Combine(Directory, "mtmd.dll");
    public string? Version { get; set; }
    public long SizeBytes { get; set; }
    public string? SourceUrl { get; set; }
    public string? ETag { get; set; }
    public bool Compatible { get; set; } = true;
    public string? Reason { get; set; }
    /// <summary>Loaded by the running process.</summary>
    public bool Active { get; set; }
    /// <summary>Chosen in the settings (takes effect after a restart when it differs from the active one).</summary>
    public bool Selected { get; set; }
    public bool Recommended { get; set; }
}

/// <summary>
/// Lists, selects, installs and updates the native inference runtimes. The native library is process wide, so the
/// selection is applied when the backend initialises (next start).
/// </summary>
public static class RuntimeManager
{
    public static string UserRoot => Path.Combine(AppPaths.Root, "runtimes");

    private static string BundledRoot =>
        Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");

    /// <summary>What the CPU can run (AVX levels) and whether a Vulkan loader is present.</summary>
    public static (bool Avx, bool Avx2, bool Avx512, bool Vulkan) Survey()
    {
        bool vulkan = false;
        try { vulkan = File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll")); } catch { }
        return (Avx.IsSupported, Avx2.IsSupported, Avx512F.IsSupported, vulkan);
    }

    private static void CheckCompat(RuntimeInfo r, (bool Avx, bool Avx2, bool Avx512, bool Vulkan) s, bool hasGpu)
    {
        r.Compatible = true; r.Reason = null;
        switch (r.Variant)
        {
            case "avx": if (!s.Avx) Fail(r, "this CPU has no AVX"); break;
            case "avx2": if (!s.Avx2) Fail(r, "this CPU has no AVX2"); break;
            case "avx512": if (!s.Avx512) Fail(r, "this CPU has no AVX-512"); break;
            case "vulkan":
                if (!s.Vulkan) Fail(r, "no Vulkan loader (vulkan-1.dll) found; install a GPU driver");
                else if (!hasGpu) Fail(r, "no GPU detected");
                break;
        }
        if (!File.Exists(r.LlamaPath)) Fail(r, "llama.dll is missing");
    }

    private static void Fail(RuntimeInfo r, string why) { r.Compatible = false; r.Reason = why; }

    public static IReadOnlyList<RuntimeInfo> List()
    {
        var list = new List<RuntimeInfo>();
        var survey = Survey();
        bool hasGpu = Hardware.Detect().Gpus.Count > 0;

        if (System.IO.Directory.Exists(BundledRoot))
            foreach (var dir in System.IO.Directory.GetDirectories(BundledRoot).OrderBy(d => d))
            {
                string v = Path.GetFileName(dir).ToLowerInvariant();
                if (!File.Exists(Path.Combine(dir, "llama.dll"))) continue;
                var r = new RuntimeInfo
                {
                    Id = "bundled:" + v, Source = "bundled", Directory = dir, Variant = v,
                    Kind = v == "vulkan" ? "vulkan" : "cpu",
                    Name = v == "vulkan" ? "Vulkan (GPU)" : "CPU " + v.ToUpperInvariant(),
                    SizeBytes = DirSize(dir), Version = FileVersion(Path.Combine(dir, "llama.dll")),
                };
                CheckCompat(r, survey, hasGpu);
                list.Add(r);
            }

        if (System.IO.Directory.Exists(UserRoot))
            foreach (var dir in System.IO.Directory.GetDirectories(UserRoot).OrderBy(d => d))
            {
                string name = Path.GetFileName(dir);
                var meta = ReadMeta(dir);
                var r = new RuntimeInfo
                {
                    Id = "user:" + name, Source = "user", Directory = dir, Name = meta?.Name ?? name,
                    Kind = meta?.Kind ?? (File.Exists(Path.Combine(dir, "ggml-vulkan.dll")) ? "vulkan" : "cpu"),
                    Variant = meta?.Variant ?? (File.Exists(Path.Combine(dir, "ggml-vulkan.dll")) ? "vulkan" : "custom"),
                    SourceUrl = meta?.SourceUrl, ETag = meta?.ETag,
                    SizeBytes = DirSize(dir), Version = FileVersion(Path.Combine(dir, "llama.dll")),
                };
                CheckCompat(r, survey, hasGpu);
                list.Add(r);
            }

        // recommendation: Vulkan when a GPU is usable, else the widest CPU build the CPU supports
        var best = hasGpu ? list.FirstOrDefault(r => r.Source == "bundled" && r.Variant == "vulkan" && r.Compatible) : null;
        best ??= new[] { "avx512", "avx2", "avx", "noavx" }
            .Select(v => list.FirstOrDefault(r => r.Source == "bundled" && r.Variant == v && r.Compatible)).FirstOrDefault(r => r != null);
        if (best != null) best.Recommended = true;

        string selected = AppSettings.Current.Runtime;
        foreach (var r in list)
        {
            r.Selected = r.Id.Equals(selected, StringComparison.OrdinalIgnoreCase);
            r.Active = NativeBackend.IsInitialized
                       && (r.Id.Equals(NativeBackend.ActiveRuntimeId, StringComparison.OrdinalIgnoreCase)
                           || NativeBackend.ActiveRuntimeId == "bundled:auto-cpu" && r.Recommended && r.Kind == "cpu");
        }
        return list;
    }

    public static RuntimeInfo? Find(string id) =>
        List().FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || r.Name.Equals(id, StringComparison.OrdinalIgnoreCase)
                                   || r.Id.EndsWith(":" + id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Remembers the choice ("auto" lets the app pick). Effective after a restart.</summary>
    public static RuntimeInfo? Select(string id)
    {
        var s = AppSettings.Current;
        if (id.Equals("auto", StringComparison.OrdinalIgnoreCase)) { s.Runtime = "auto"; s.Save(); return null; }
        var r = Find(id) ?? throw new FileNotFoundException($"No runtime '{id}'. Use 'runtime list'.");
        if (!r.Compatible) throw new InvalidOperationException($"{r.Name} cannot run on this machine: {r.Reason}.");
        s.Runtime = r.Id;
        s.Save();
        return r;
    }

    // ------------------------------------------------------------------ installing / updating

    private class Meta { public string? Name { get; set; } public string? Kind { get; set; } public string? Variant { get; set; } public string? SourceUrl { get; set; } public string? ETag { get; set; } }

    private static Meta? ReadMeta(string dir)
    {
        try
        {
            var f = Path.Combine(dir, "runtime.json");
            return File.Exists(f) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(f), Json.Options) : null;
        }
        catch { return null; }
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    /// <summary>
    /// Installs a runtime from a .zip (local path or URL) or a folder holding llama.dll and the ggml libraries.
    /// A runtime must match the binding this app was built against; an incompatible one is rejected at start-up and
    /// the app falls back to a bundled build.
    /// </summary>
    public static async Task<RuntimeInfo> InstallAsync(string source, string? name, CancellationToken ct = default)
    {
        string staging = Path.Combine(Path.GetTempPath(), "llmhost-rt-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(staging);
        string? url = null, etag = null;
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                url = source;
                string zip = Path.Combine(staging, "runtime.zip");
                using var resp = await HuggingFace.Client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                etag = resp.Headers.ETag?.Tag ?? resp.Content.Headers.LastModified?.ToString("o");
                await using (var fs = File.Create(zip)) await resp.Content.CopyToAsync(fs, ct);
                ZipFile.ExtractToDirectory(zip, Path.Combine(staging, "x"));
            }
            else if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(source, Path.Combine(staging, "x"));
            else if (System.IO.Directory.Exists(source))
                CopyDir(source, Path.Combine(staging, "x"));
            else throw new FileNotFoundException($"'{source}' is not a .zip, a folder or a URL.");

            // the folder that holds llama.dll
            var llama = System.IO.Directory.EnumerateFiles(Path.Combine(staging, "x"), "llama.dll", SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new InvalidDataException("No llama.dll found in that package.");
            string root = Path.GetDirectoryName(llama)!;
            if (!System.IO.Directory.EnumerateFiles(root, "ggml*.dll").Any())
                throw new InvalidDataException("The package has llama.dll but no ggml*.dll libraries.");

            string id = Sanitize(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(url ?? source.TrimEnd('\\', '/')) : name);
            string dest = Path.Combine(UserRoot, id);
            if (System.IO.Directory.Exists(dest)) System.IO.Directory.Delete(dest, true);
            CopyDir(root, dest);
            bool vulkan = File.Exists(Path.Combine(dest, "ggml-vulkan.dll"));
            File.WriteAllText(Path.Combine(dest, "runtime.json"), JsonSerializer.Serialize(new Meta
            {
                Name = id, Kind = vulkan ? "vulkan" : "cpu", Variant = vulkan ? "vulkan" : "custom", SourceUrl = url, ETag = etag,
            }, Json.Options));
            LogBuffer.Add("runtime", $"Installed runtime '{id}' from {url ?? source}");
            return Find("user:" + id)!;
        }
        finally { try { System.IO.Directory.Delete(staging, true); } catch { } }
    }

    /// <summary>Re-downloads user runtimes whose source changed. Returns the ids that were updated.</summary>
    public static async Task<IReadOnlyList<string>> UpdateAsync(CancellationToken ct = default)
    {
        var updated = new List<string>();
        foreach (var r in List().Where(r => r.Source == "user" && !string.IsNullOrEmpty(r.SourceUrl)))
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, r.SourceUrl);
            using var resp = await HuggingFace.Client.SendAsync(head, ct);
            if (!resp.IsSuccessStatusCode) continue;
            string? tag = resp.Headers.ETag?.Tag ?? resp.Content.Headers.LastModified?.ToString("o");
            if (tag != null && tag == r.ETag) continue;
            await InstallAsync(r.SourceUrl!, Path.GetFileName(r.Directory), ct);
            updated.Add(r.Id);
        }
        return updated;
    }

    public static bool Remove(string id)
    {
        var r = Find(id);
        if (r == null || r.Source != "user") return false;
        if (r.Active) throw new InvalidOperationException("That runtime is in use; switch to another one and restart first.");
        System.IO.Directory.Delete(r.Directory, true);
        if (AppSettings.Current.Runtime.Equals(r.Id, StringComparison.OrdinalIgnoreCase)) { AppSettings.Current.Runtime = "auto"; AppSettings.Current.Save(); }
        return true;
    }

    private static void CopyDir(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);
        foreach (var f in System.IO.Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }

    private static long DirSize(string d) => System.IO.Directory.EnumerateFiles(d).Sum(f => new FileInfo(f).Length);

    private static string? FileVersion(string path)
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var s = v.ProductVersion ?? v.FileVersion;
            return string.IsNullOrWhiteSpace(s) || s == "0.0.0.0" ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd") : s;
        }
        catch { return null; }
    }
}
