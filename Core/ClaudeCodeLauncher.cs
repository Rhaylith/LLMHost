using System.Diagnostics;
using System.Text;

namespace LLMHostProject.Core;

/// <summary>
/// Opens a PowerShell window with the environment variables that make Claude Code talk to this server
/// (ANTHROPIC_BASE_URL, ANTHROPIC_AUTH_TOKEN, ANTHROPIC_MODEL ...) and starts <c>claude</c> in it.
/// </summary>
public static class ClaudeCodeLauncher
{
    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    public static void Launch(string baseUrl, string authToken, string model, string? workingDirectory)
    {
        // the server may be bound to 0.0.0.0: always connect through the loopback address
        var u = new Uri(baseUrl);
        string url = $"{u.Scheme}://127.0.0.1:{u.Port}";

        string dir = !string.IsNullOrWhiteSpace(workingDirectory) ? workingDirectory.Trim().Trim('"')
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"The folder '{dir}' does not exist.");

        RefreshConfiguredFiles(baseUrl, authToken, model, dir);
        var sb = new StringBuilder();
        sb.AppendLine("$Host.UI.RawUI.WindowTitle = 'Claude Code - ' + " + Q(model));
        // The app may have been started before Claude Code was installed (or from a service / start-up entry), so this
        // process's PATH can be stale or short. Rebuild it from the registry the way a fresh login would.
        sb.AppendLine("$m = [Environment]::GetEnvironmentVariable('Path','Machine'); $u = [Environment]::GetEnvironmentVariable('Path','User')");
        sb.AppendLine("""$extra = @("$env:USERPROFILE\.local\bin", "$env:APPDATA\npm", "$env:LOCALAPPDATA\Programs\claude") | Where-Object { Test-Path $_ }""");
        sb.AppendLine("$env:Path = ((@($u, $m, $env:Path) + $extra) -join ';' -split ';' | Where-Object { $_ } | Select-Object -Unique) -join ';'");
        sb.AppendLine($"$env:ANTHROPIC_BASE_URL = {Q(url)}");
        sb.AppendLine($"$env:ANTHROPIC_AUTH_TOKEN = {Q(authToken)}");
        sb.AppendLine($"$env:ANTHROPIC_MODEL = {Q(model)}");
        sb.AppendLine($"$env:ANTHROPIC_SMALL_FAST_MODEL = {Q(model)}");
        sb.AppendLine($"$env:ANTHROPIC_DEFAULT_HAIKU_MODEL = {Q(model)}");
        sb.AppendLine($"$env:ANTHROPIC_DEFAULT_SONNET_MODEL = {Q(model)}");
        sb.AppendLine($"$env:ANTHROPIC_DEFAULT_OPUS_MODEL = {Q(model)}");
        sb.AppendLine($"$env:CLAUDE_CODE_SUBAGENT_MODEL = {Q(model)}");
        sb.AppendLine("$env:CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC = '1'");
        sb.AppendLine($"Set-Location -LiteralPath {Q(dir)}");
        sb.AppendLine($"Write-Host 'Claude Code -> {url} using model {model.Replace("'", "")}' -ForegroundColor Cyan");
        sb.AppendLine("if (Get-Command claude -ErrorAction SilentlyContinue) { claude }");
        sb.AppendLine("else { Write-Host 'The claude command was not found. Install it with:  irm https://claude.ai/install.ps1 | iex   (then reopen this window)' -ForegroundColor Yellow }");

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,                  // a visible, independent console window
            WorkingDirectory = dir,
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);
        Process.Start(psi);
    }


    /// <summary>
    /// A settings file written earlier by <see cref="Configure"/> overrides the process environment, so a launch with a
    /// different model would still use the old one. Bring any such file (project and user level) up to date.
    /// Files without our entries are left alone; nothing is created.
    /// </summary>
    private static void RefreshConfiguredFiles(string baseUrl, string authToken, string model, string workingDirectory)
    {
        var candidates = new List<string?> { workingDirectory, null };
        string userLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.local.json");
        foreach (var dir in candidates)
        {
            string path = SettingsPath(dir);
            try
            {
                if (!File.Exists(path)) continue;
                if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonObject root
                    || root["env"] is not System.Text.Json.Nodes.JsonObject env
                    || env["ANTHROPIC_BASE_URL"] == null && env["ANTHROPIC_MODEL"] == null) continue;
                Configure(baseUrl, authToken, model, dir);
                LogBuffer.Add("server", $"Updated Claude Code settings in {path} to model {model}");
            }
            catch (Exception ex) { LogBuffer.Add("server", $"Could not update {path}: {ex.Message}"); }
        }
        // the user-level local file (~/.claude/settings.local.json) is also read by Claude Code
        try
        {
            if (File.Exists(userLocal)
                && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(userLocal)) is System.Text.Json.Nodes.JsonObject r2
                && r2["env"] is System.Text.Json.Nodes.JsonObject e2 && e2["ANTHROPIC_MODEL"] != null)
            {
                Configure(baseUrl, authToken, model, Path.GetDirectoryName(Path.GetDirectoryName(userLocal)));
            }
        }
        catch { }
    }
    // ------------------------------------------------------------------ settings-file based (Claude Code desktop app)

    private static readonly string[] ManagedKeys =
    {
        "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
        "CLAUDE_CODE_SUBAGENT_MODEL", "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC",
    };

    /// <summary>
    /// The settings file the Claude Code CLI and desktop app both read: <c>&lt;folder&gt;/.claude/settings.local.json</c>
    /// (only for that project, normally not committed) or, without a folder, the user-level <c>~/.claude/settings.json</c>.
    /// </summary>
    public static string SettingsPath(string? directory) => string.IsNullOrWhiteSpace(directory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json")
        : Path.Combine(directory.Trim().Trim('"'), ".claude", "settings.local.json");

    /// <summary>Adds the environment values to the settings file, keeping everything else in it.</summary>
    public static string Configure(string baseUrl, string authToken, string model, string? directory)
    {
        var u = new Uri(baseUrl);
        string url = $"{u.Scheme}://127.0.0.1:{u.Port}";
        string path = SettingsPath(directory);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory.Trim().Trim('"')))
            throw new DirectoryNotFoundException($"The folder '{directory}' does not exist.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        System.Text.Json.Nodes.JsonObject root;
        try
        {
            root = File.Exists(path) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject o
                ? o : new System.Text.Json.Nodes.JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException($"{path} is not valid JSON; fix or remove it first so no settings are lost.");
        }
        var env = root["env"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        env["ANTHROPIC_BASE_URL"] = url;
        env["ANTHROPIC_AUTH_TOKEN"] = authToken;
        env["ANTHROPIC_MODEL"] = model;
        env["ANTHROPIC_SMALL_FAST_MODEL"] = model;
        env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = model;
        // the app's model picker offers Sonnet / Opus / Haiku: make every alias resolve to the local model
        env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model;
        env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model;
        env["CLAUDE_CODE_SUBAGENT_MODEL"] = model;
        root["model"] = model;
        env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        root["env"] = env;
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Removes only the values <see cref="Configure"/> wrote. Returns false when there was nothing to remove.</summary>
    public static bool Remove(string? directory)
    {
        string path = SettingsPath(directory);
        if (!File.Exists(path)) return false;
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject;
        if (root?["env"] is not System.Text.Json.Nodes.JsonObject env) return false;
        bool any = false;
        string? ours = env["ANTHROPIC_MODEL"]?.GetValue<string>();
        foreach (var k in ManagedKeys) any |= env.Remove(k);
        if (ours != null && root["model"]?.GetValue<string>() == ours) { root.Remove("model"); any = true; }
        if (!any) return false;
        if (env.Count == 0) root.Remove("env");
        if (root.Count == 0) File.Delete(path);
        else File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return true;
    }
}
