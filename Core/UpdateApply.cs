using System.Diagnostics;
using System.IO.Compression;

// Shared by the app (tests, "install now") and the small updater program (Updater/), which is compiled from this very file.
namespace LLMHostProject.Core;

/// <summary>What the updater has to do.</summary>
/// <param name="TargetDir">The installation folder that is replaced (zip packages).</param>
/// <param name="Kind">zip | msi | exe</param>
/// <param name="WaitPid">Process that has to exit first (the app that started the update); 0 = none.</param>
/// <param name="BeforeCommand">Command line run before the files are replaced (for example stopping the background service), or null.</param>
/// <param name="AfterCommand">Command line that starts the program again, or null.</param>
public record UpdatePlan(string TargetDir, string PackagePath, string Kind, int WaitPid, string? BeforeCommand, string? AfterCommand, string Version = "");

public enum UpdateOutcome { Updated, FailedRolledBack, FailedNothingChanged }
public record UpdateResult(UpdateOutcome Outcome, string Message);

/// <summary>The outside world of the updater, replaceable in tests.</summary>
public interface IUpdateEnv
{
    bool WaitForExit(int pid, TimeSpan timeout);
    int Run(string commandLine, TimeSpan timeout);
    /// <summary>Starts the program; true when it is still running after <paramref name="grace"/> (it did not crash at once).</summary>
    bool StartAndSurvives(string commandLine, TimeSpan grace);
    void Log(string message);
}

public sealed class ProcessUpdateEnv : IUpdateEnv
{
    private readonly Action<string> _log;
    public ProcessUpdateEnv(Action<string> log) => _log = log;
    public void Log(string m) => _log(m);

    public bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return true;
        try { using var p = Process.GetProcessById(pid); return p.WaitForExit(timeout); }
        catch (ArgumentException) { return true; }                       // already gone
    }

    private static (string Exe, string Args) Split(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"')) { int end = cmd.IndexOf('"', 1); return (cmd[1..end], cmd[(end + 1)..].Trim()); }
        int sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    public int Run(string commandLine, TimeSpan timeout)
    {
        var (exe, args) = Split(commandLine);
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Could not start " + exe);
        if (!p.WaitForExit(timeout)) { try { p.Kill(true); } catch { } return -1; }
        return p.ExitCode;
    }

    public bool StartAndSurvives(string commandLine, TimeSpan grace)
    {
        var (exe, args) = Split(commandLine);
        var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        if (p == null) return false;
        return !p.WaitForExit(grace);
    }
}

/// <summary>
/// Replaces an installation: waits for the app to exit, puts the new files in place (keeping what they replace), starts the program again and
/// goes back to the old files when something fails or the new program does not stay up.
/// </summary>
public static class UpdateApply
{
    public static TimeSpan RestartGrace { get; set; } = TimeSpan.FromSeconds(8);

    public static UpdateResult Apply(UpdatePlan plan, IUpdateEnv env)
    {
        try
        {
            if (!File.Exists(plan.PackagePath)) return new(UpdateOutcome.FailedNothingChanged, "The downloaded package is missing.");
            env.Log($"Waiting for process {plan.WaitPid} to exit");
            if (!env.WaitForExit(plan.WaitPid, TimeSpan.FromSeconds(60))) return new(UpdateOutcome.FailedNothingChanged, "The app did not close in time.");
            if (plan.BeforeCommand != null) { env.Log("Running: " + plan.BeforeCommand); try { env.Run(plan.BeforeCommand, TimeSpan.FromSeconds(60)); } catch (Exception ex) { env.Log("Before-command failed: " + ex.Message); } }

            UpdateResult result = plan.Kind.ToLowerInvariant() switch
            {
                "zip" => ApplyZip(plan, env),
                "msi" => RunInstaller($"msiexec /i \"{plan.PackagePath}\" /qn /norestart", env),
                "exe" => RunInstaller($"\"{plan.PackagePath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", env),
                _ => new(UpdateOutcome.FailedNothingChanged, "Unknown package type " + plan.Kind),
            };
            return result;
        }
        catch (Exception ex) { env.Log("Update failed: " + ex.Message); return new(UpdateOutcome.FailedNothingChanged, ex.Message); }
    }

    private static UpdateResult RunInstaller(string command, IUpdateEnv env)
    {
        env.Log("Running: " + command);
        int code = env.Run(command, TimeSpan.FromMinutes(10));
        return code is 0 or 3010 ? new(UpdateOutcome.Updated, "Installed.") : new(UpdateOutcome.FailedNothingChanged, $"The installer ended with code {code}.");
    }

    private static UpdateResult ApplyZip(UpdatePlan plan, IUpdateEnv env)
    {
        string target = Path.GetFullPath(plan.TargetDir);
        string staging = target.TrimEnd('\\', '/') + ".update", backup = target.TrimEnd('\\', '/') + ".backup";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        Directory.CreateDirectory(staging);

        // 1. unpack next to the installation, refusing paths that leave the folder
        using (var zip = ZipFile.OpenRead(plan.PackagePath))
        {
            string? root = CommonRoot(zip);
            foreach (var e in zip.Entries)
            {
                string rel = root != null && e.FullName.StartsWith(root) ? e.FullName[root.Length..] : e.FullName;
                if (rel.Length == 0) continue;
                string dest = Path.GetFullPath(Path.Combine(staging, rel));
                if (!dest.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return Fail(env, staging, "The package contains a path outside the folder.");
                if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
            }
        }
        if (!File.Exists(Path.Combine(staging, "llmhost.exe")) && !File.Exists(Path.Combine(staging, "llmhost.dll"))) return Fail(env, staging, "The package does not contain the program (llmhost.exe).");

        // 2. copy over, remembering what was replaced and what is new
        var replaced = new List<string>(); var added = new List<string>();
        try
        {
            foreach (var src in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(staging, src), dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    string keep = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                    File.Copy(dest, keep, true);
                    replaced.Add(rel);
                }
                else added.Add(rel);
                CopyWithRetry(src, dest);
            }
        }
        catch (Exception ex)
        {
            env.Log("Copying failed: " + ex.Message);
            Rollback(target, backup, replaced, added, env);
            TryStart(plan, env, false);
            return new(UpdateOutcome.FailedRolledBack, "Could not replace the files (" + ex.Message + "); the old version was restored.");
        }
        try { Directory.Delete(staging, true); } catch { }

        // 3. start the new program; when it dies at once, go back
        if (plan.AfterCommand != null && !env.StartAndSurvives(plan.AfterCommand, RestartGrace))
        {
            env.Log("The new version did not stay running: going back");
            Rollback(target, backup, replaced, added, env);
            env.StartAndSurvives(plan.AfterCommand, TimeSpan.FromSeconds(1));
            return new(UpdateOutcome.FailedRolledBack, "The new version did not start; the old version was restored.");
        }
        env.Log("Updated to " + plan.Version);
        return new(UpdateOutcome.Updated, "Updated.");
    }

    private static void TryStart(UpdatePlan plan, IUpdateEnv env, bool wait) { if (plan.AfterCommand != null) { try { env.StartAndSurvives(plan.AfterCommand, TimeSpan.FromSeconds(1)); } catch { } } }

    private static UpdateResult Fail(IUpdateEnv env, string staging, string message)
    {
        env.Log(message);
        try { Directory.Delete(staging, true); } catch { }
        return new(UpdateOutcome.FailedNothingChanged, message);
    }

    /// <summary>The single top-level folder all entries share ("LLMHost/"), which is removed when unpacking; null when there is none.</summary>
    private static string? CommonRoot(ZipArchive zip)
    {
        var tops = zip.Entries.Select(e => e.FullName.Split('/')[0]).Distinct().ToList();
        if (tops.Count != 1 || zip.Entries.Any(e => e.FullName == tops[0])) return null;       // a file at the top level: no common folder
        return tops[0] + "/";
    }

    private static void CopyWithRetry(string src, string dest)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Copy(src, dest, true); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(400); }
        }
    }

    private static void Rollback(string target, string backup, List<string> replaced, List<string> added, IUpdateEnv env)
    {
        foreach (var rel in replaced)
        {
            try { CopyWithRetry(Path.Combine(backup, rel), Path.Combine(target, rel)); } catch (Exception ex) { env.Log("Could not restore " + rel + ": " + ex.Message); }
        }
        foreach (var rel in added) { try { File.Delete(Path.Combine(target, rel)); } catch { } }
    }
}
