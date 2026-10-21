using System.Diagnostics;
using LLMHostProject.Core;

namespace LLMHostProject.Server;

/// <summary>
/// Turns a video file into a few still frames that can be sent to a vision model as ordinary images (in order).
/// Uses ffmpeg, found next to the app, on the PATH or via <c>vision.ffmpegPath</c>.
/// </summary>
public static class VideoFrames
{
    public const int DefaultMaxFrames = 8;

    /// <summary>Locates ffmpeg, or returns null.</summary>
    public static string? FindFfmpeg()
    {
        var configured = AppSettings.Current.Vision.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim('"'), exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>ffmpeg arguments: <paramref name="fps"/> frames per second, at most <paramref name="maxFrames"/>, longest edge <paramref name="maxEdge"/>.</summary>
    public static IReadOnlyList<string> BuildArguments(string video, string outputPattern, double fps, int maxFrames, int maxEdge)
    {
        string scale = maxEdge > 0 ? $",scale='if(gt(iw,ih),min({maxEdge},iw),-2)':'if(gt(ih,iw),min({maxEdge},ih),-2)'" : "";
        return new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", video,
            "-vf", FormattableString.Invariant($"fps={fps:0.####}{scale}"),
            "-frames:v", maxFrames.ToString(), "-q:v", "3", outputPattern,
        };
    }

    /// <summary>Extracts up to <paramref name="maxFrames"/> JPEG frames. Throws with a helpful message when ffmpeg is missing or fails.</summary>
    public static async Task<IReadOnlyList<byte[]>> ExtractAsync(string video, double fps = 1, int maxFrames = DefaultMaxFrames, int maxEdge = 0,
        CancellationToken ct = default)
    {
        if (!File.Exists(video)) throw new FileNotFoundException($"Video not found: {video}");
        if (fps <= 0 || fps > 30) throw new ArgumentException("fps must be between 0 and 30.");
        maxFrames = Math.Clamp(maxFrames, 1, ImageInput.MaxImagesPerRequest);
        string ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "Video input needs ffmpeg, which was not found. Install it (e.g. 'winget install ffmpeg'), put ffmpeg.exe next to llmhost.exe, or set vision.ffmpegPath.");

        string dir = Path.Combine(Path.GetTempPath(), "llmhost-frames-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in BuildArguments(video, Path.Combine(dir, "f%03d.jpg"), fps, maxFrames, maxEdge)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not be started.");
            var err = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg could not read the video: " + (await err).Trim());
            var frames = Directory.EnumerateFiles(dir, "f*.jpg").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
            if (frames.Count == 0) throw new InvalidOperationException("No frames could be extracted from the video.");
            return frames;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
