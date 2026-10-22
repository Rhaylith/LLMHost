using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LLMHostProject.Core;

/// <summary>A newer version announced by the update feed, with the package to install when the feed lists one.</summary>
public record UpdateInfo(string Version, string Url, string? Notes, ReleaseAsset? Asset = null, bool Prerelease = false, DateTime? Published = null);

/// <summary>Checks the feed for the chosen channel, downloads and verifies the package, and hands over to the updater program.</summary>
public static class UpdateService
{
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";

    public static UpdateInfo? Latest { get; private set; }
    public static DateTime? LastChecked { get; private set; }
    public static string? LastError { get; private set; }
    /// <summary>For tests: where the feed text comes from.</summary>
    public static Func<string, CancellationToken, Task<string>>? FeedFetcher { get; set; }

    public static bool IsNewer(string candidate) => SemVer.TryParse(candidate, out var c) && SemVer.TryParse(CurrentVersion, out var cur) && c > cur;

    /// <summary>The update the feed offers for the channel in the settings, if any. Nothing is requested without a configured feed.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default, string? currentOverride = null)
    {
        string? feed = AppSettings.Current.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feed)) { LastError = "No update feed URL is configured."; return null; }
        try
        {
            string text;
            if (FeedFetcher != null) text = await FeedFetcher(feed, ct);
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("llmhost/" + CurrentVersion);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, application/json");
                text = await http.GetStringAsync(feed, ct);
            }
            var releases = ReleaseFeed.Parse(text);
            LastChecked = DateTime.UtcNow; LastError = null;
            var cur = SemVer.Parse(currentOverride ?? CurrentVersion);
            var best = ReleaseFeed.Newest(releases, AppSettings.Current.Update.Channel, cur);
            if (best == null) { Latest = null; return null; }
            var asset = ReleaseFeed.PickAsset(best);
            // a checksum file next to the package (SHA256SUMS) supplies the hash when the feed did not
            if (asset != null && asset.Sha256 == null && best.Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase)) is { } sums)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    string body = FeedFetcher != null ? await FeedFetcher(sums.Url, ct) : await http.GetStringAsync(sums.Url, ct);
                    if (ReleaseFeed.ParseChecksums(body).TryGetValue(asset.Name, out var h)) asset = asset with { Sha256 = h };
                }
                catch { /* without a hash the package can be downloaded by hand but not installed from the app */ }
            }
            Latest = new UpdateInfo(best.Version, asset?.Url ?? feed, best.Notes, asset, best.Prerelease, best.Published);
            return Latest;
        }
        catch (Exception ex) { LastChecked = DateTime.UtcNow; LastError = ex.Message; Latest = null; return null; }
    }

    // ---- verification

    public static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Whether the file has a valid Authenticode signature chain (WinVerifyTrust).</summary>
    public static bool HasValidSignature(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /* none */, fdwRevocationChecks = 0, dwUnionChoice = 1 /* file */,
                pFile = pFile, dwStateAction = 0, dwProvFlags = 0x80 /* cache only url retrieval */,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO { public uint cbStruct; [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks;
        public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference; public uint dwProvFlags; public uint dwUIContext;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    /// <summary>Downloads the package to a temporary folder and checks it: the SHA-256 of the feed, and for installers a valid signature. The file is deleted when a check fails.</summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, IProgress<double>? progress = null, CancellationToken ct = default, HttpClient? client = null)
    {
        if (string.IsNullOrEmpty(asset.Sha256)) throw new InvalidOperationException("The feed does not publish a SHA-256 for this package, so it cannot be installed from here. Download it from the release page.");
        if (!asset.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && client == null) throw new InvalidOperationException("Packages are only downloaded over https.");
        string dir = Path.Combine(Path.GetTempPath(), "llmhost-update", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Path.GetFileName(asset.Name));
        using var own = client == null ? new HttpClient { Timeout = TimeSpan.FromMinutes(30) } : null;
        var http = client ?? own!;
        using (var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buf = new byte[1 << 20]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n; if (total > 0) progress?.Report((double)done / total); }
        }
        if (!Sha256Of(path).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) { TryDelete(dir); throw new InvalidDataException("The downloaded package does not match its SHA-256: it was damaged or altered. Nothing was installed."); }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".msi" or ".exe" && AppSettings.Current.Update.RequireSignature && !HasValidSignature(path)) { TryDelete(dir); throw new InvalidDataException("The installer has no valid digital signature. Nothing was installed."); }
        return path;
    }

    private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { } }

    // ---- handing over

    /// <summary>How the program runs now, which decides what is stopped and started around the update.</summary>
    public enum RunMode { Window, Daemon, Service }

    public static UpdatePlan BuildPlan(string package, string version, RunMode mode, string? installDir = null, string? exePath = null)
    {
        string dir = installDir ?? AppContext.BaseDirectory.TrimEnd('\\', '/');
        string exe = exePath ?? Environment.ProcessPath ?? Path.Combine(dir, "llmhost.exe");
        string kind = Path.GetExtension(package).TrimStart('.').ToLowerInvariant();
        string? before = mode switch { RunMode.Service => "sc stop LLMHost", RunMode.Daemon => $"\"{exe}\" daemon down", _ => null };
        string? after = mode switch { RunMode.Service => "sc start LLMHost", RunMode.Daemon => $"\"{exe}\" daemon up", _ => $"\"{exe}\"" };
        return new UpdatePlan(dir, package, kind, Environment.ProcessId, before, after, version);
    }

    /// <summary>Copies the updater to a temporary folder (so it can replace its own installation) and starts it. The caller must then close the app.</summary>
    public static void StartUpdater(UpdatePlan plan)
    {
        string src = Path.Combine(AppContext.BaseDirectory, "updater");
        if (!File.Exists(Path.Combine(src, "LLMHost.Updater.exe"))) throw new FileNotFoundException("The updater program is missing from this installation.");
        string tmp = Path.Combine(Path.GetTempPath(), "llmhost-updater-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)), true);
        string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
        var args = new List<string> { "--target", Q(plan.TargetDir), "--package", Q(plan.PackagePath), "--kind", plan.Kind, "--pid", plan.WaitPid.ToString(), "--version", Q(plan.Version) };
        if (plan.BeforeCommand != null) { args.Add("--before"); args.Add(Q(plan.BeforeCommand)); }
        if (plan.AfterCommand != null) { args.Add("--after"); args.Add(Q(plan.AfterCommand)); }
        Process.Start(new ProcessStartInfo(Path.Combine(tmp, "LLMHost.Updater.exe"), string.Join(" ", args)) { UseShellExecute = false, CreateNoWindow = true });
    }
}
