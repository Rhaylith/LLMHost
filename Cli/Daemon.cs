using System.Diagnostics;
using System.Text.Json;
using LLMHostProject.Core;

namespace LLMHostProject.Cli;

/// <summary>What a running daemon leaves behind so other invocations can find and control it.</summary>
public class DaemonInfo
{
    public int Pid { get; set; }
    public string Url { get; set; } = "";
    /// <summary>Per-run secret that lets the control commands act as admin even when token authentication is on.</summary>
    public string Token { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string Exe { get; set; } = "";
}

/// <summary>
/// Background server without a window: <c>daemon up|down|status|update</c>, plus start-up integration
/// (a scheduled task at logon, or a Windows service). The daemon is the normal <c>serve</c> mode run detached.
/// </summary>
public static class Daemon
{
    public static string InfoFile => Path.Combine(AppPaths.Root, "daemon.json");
    private const string RunKeyPath = @"SoftwareMicrosoftWindowsCurrentVersionRun";
    private const string RunValueName = "LLMHostDaemon";
    public const string ServiceName = "LLMHostDaemon";

    public static DaemonInfo? Read()
    {
        try { return File.Exists(InfoFile) ? JsonSerializer.Deserialize<DaemonInfo>(File.ReadAllText(InfoFile), Json.Options) : null; }
        catch { return null; }
    }

    public static void Write(DaemonInfo info)
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(InfoFile, JsonSerializer.Serialize(info, Json.Options));
    }

    public static void Clear(int pid)
    {
        try { if (Read()?.Pid == pid) File.Delete(InfoFile); } catch { }
    }

    public static bool IsAlive(DaemonInfo i)
    {
        try { using var p = Process.GetProcessById(i.Pid); return !p.HasExited; }
        catch { return false; }
    }

    private static HttpClient Http(DaemonInfo i)
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8), BaseAddress = new Uri(i.Url) };
        if (!string.IsNullOrEmpty(i.Token)) c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", i.Token);
        return c;
    }

    /// <summary>Running daemon (process alive and answering), or null.</summary>
    public static async Task<(DaemonInfo Info, JsonElement Status)?> ProbeAsync()
    {
        var i = Read();
        if (i == null || !IsAlive(i)) return null;
        try
        {
            using var http = Http(i);
            using var r = await http.GetAsync("/api/status");
            if (!r.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return (i, doc.RootElement.Clone());
        }
        catch { return null; }
    }

    /// <summary>Starts the detached server process and waits until it answers.</summary>
    public static async Task<DaemonInfo> UpAsync(IEnumerable<string> serveArgs, TimeSpan? wait = null)
    {
        var running = await ProbeAsync();
        if (running != null) return running.Value.Info;

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the executable.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--daemon");
        foreach (var a in serveArgs) psi.ArgumentList.Add(a);
        if (File.Exists(InfoFile)) File.Delete(InfoFile);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the daemon process.");
        // detach: the daemon owns its streams only until it is up; drain so it never blocks on a full pipe
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(45));
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited) throw new InvalidOperationException($"The daemon exited immediately (code {proc.ExitCode}). Run 'serve' in a terminal to see why.");
            var p = await ProbeAsync();
            if (p != null) return p.Value.Info;
            await Task.Delay(300);
        }
        throw new TimeoutException("The daemon did not become ready in time.");
    }

    /// <summary>Asks the daemon to stop; kills it if it does not leave within a few seconds.</summary>
    public static async Task<bool> DownAsync()
    {
        var i = Read();
        if (i == null) return false;
        if (!IsAlive(i)) { try { File.Delete(InfoFile); } catch { } return false; }
        try
        {
            using var http = Http(i);
            using var r = await http.PostAsync("/api/shutdown", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        }
        catch { /* fall through to the wait / kill below */ }
        for (int n = 0; n < 40 && IsAlive(i); n++) await Task.Delay(250);
        if (IsAlive(i))
        {
            try { using var p = Process.GetProcessById(i.Pid); p.Kill(true); } catch { }
        }
        try { File.Delete(InfoFile); } catch { }
        return true;
    }

    // ------------------------------------------------------------------ start-up integration

    private static (int Code, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o.Trim());
    }

    /// <summary>Registers the daemon to start at logon (per-user Run key, no admin rights) or as a Windows service (admin).</summary>
    public static string Install(bool asService, IEnumerable<string> serveArgs)
    {
        string exe = Environment.ProcessPath!;
        string extra = string.Join(' ', serveArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        if (asService)
        {
            var (code, outp) = Run("sc.exe", "create", ServiceName, "binPath=", $"\"{exe}\" serve --daemon --service {extra}".Trim(),
                "start=", "auto", "DisplayName=", "LLMHost local LLM server");
            if (code != 0) throw new InvalidOperationException("Creating the service failed (administrator rights are needed): " + outp);
            Run("sc.exe", "description", ServiceName, "LLMHost model server (OpenAI / Anthropic compatible API)");
            return "Windows service '" + ServiceName + "' created (start it with: sc start " + ServiceName + ").";
        }
        // per-user Run key: starts at logon, no administrator rights needed
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath))
            key.SetValue(RunValueName, $"\"{exe}\" serve --daemon {extra}".Trim());
        return "Start-up entry added: the daemon starts when you log in.";
    }

    public static string Uninstall()
    {
        var msgs = new List<string>();
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            if (key?.GetValue(RunValueName) != null) { key.DeleteValue(RunValueName); msgs.Add("start-up entry removed"); }
        var (s, _) = Run("sc.exe", "delete", ServiceName);
        if (s == 0) msgs.Add("service removed");
        return msgs.Count > 0 ? string.Join(", ", msgs) + "." : "Nothing was installed.";
    }

    public static bool IsInstalledAsTask()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }
}
