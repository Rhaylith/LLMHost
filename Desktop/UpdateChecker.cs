using LLMHostProject.Core;

namespace LLMHostProject.Desktop;

/// <summary>The window's view of <see cref="UpdateService"/> (nothing is requested unless the user turned update checks on and gave a feed).</summary>
public static class UpdateChecker
{
    public static string CurrentVersion => UpdateService.CurrentVersion;
    public static UpdateInfo? Latest => UpdateService.Latest;
    public static DateTime? LastChecked => UpdateService.LastChecked;
    public static string? LastError => UpdateService.LastError;
    public static bool IsNewer(string candidate) => UpdateService.IsNewer(candidate);
    public static Task<UpdateInfo?> CheckAsync(CancellationToken ct = default) => UpdateService.CheckAsync(ct);
}
