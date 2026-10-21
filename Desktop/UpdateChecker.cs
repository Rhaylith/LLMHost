using System.Reflection;
using System.Text.Json;
using LLMHostProject.Core;

namespace LLMHostProject.Desktop;

/// <summary>A newer version announced by the update feed.</summary>
public record UpdateInfo(string Version, string Url, string? Notes);

/// <summary>
/// Looks for a newer version at a user-configured feed URL (a small JSON document: version, url, notes). Nothing is
/// requested unless the user turned update checks on and gave a feed; there is no built-in address.
/// </summary>
public static class UpdateChecker
{
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";

    public static UpdateInfo? Latest { get; private set; }
    public static DateTime? LastChecked { get; private set; }
    public static string? LastError { get; private set; }

    public static bool IsNewer(string candidate)
    {
        return Version.TryParse(Normalize(candidate), out var c) && Version.TryParse(Normalize(CurrentVersion), out var cur) && c > cur;
    }

    private static string Normalize(string v) => new string(v.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');

    /// <summary>Fetches the feed; returns the update when it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        string? feed = AppSettings.Current.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feed)) { LastError = "No update feed URL is configured."; return null; }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("llmhost/" + CurrentVersion);
            using var doc = JsonDocument.Parse(await http.GetStringAsync(feed, ct));
            var root = doc.RootElement;
            string? version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            string? url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            string? notes = root.TryGetProperty("notes", out var n) ? n.GetString() : null;
            LastChecked = DateTime.UtcNow; LastError = null;
            if (version == null) { LastError = "The feed has no \"version\"."; return null; }
            Latest = IsNewer(version) ? new UpdateInfo(version, url ?? feed, notes) : null;
            return Latest;
        }
        catch (Exception ex)
        {
            LastChecked = DateTime.UtcNow; LastError = ex.Message;
            return null;
        }
    }
}
