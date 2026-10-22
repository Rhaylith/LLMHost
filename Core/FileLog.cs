using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMHostProject.Core;

public enum LogLevel { Trace, Debug, Info, Warn, Error, Off }

public static class LogLevels
{
    public static LogLevel Parse(string? s, LogLevel fallback) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        "off" or "none" => LogLevel.Off,
        _ => fallback,
    };

    public static string Name(LogLevel l) => l.ToString().ToLowerInvariant();
}

/// <summary>Logging options (settings.json → <c>logging</c>).</summary>
public class LoggingSettings
{
    /// <summary>Minimum level written to the log files: trace, debug, info, warn, error or off.</summary>
    public string Level { get; set; } = "info";
    /// <summary>Minimum level of llama.cpp's own messages that is kept (log view and files): debug, info, warn, error or off.</summary>
    public string Native { get; set; } = "info";
    /// <summary>Log files older than this many days are deleted (0 = keep forever).</summary>
    public int RetainDays { get; set; } = 14;
    /// <summary>A log file is rolled over when it reaches this size.</summary>
    public int MaxFileMb { get; set; } = 10;
    /// <summary>Keep the (redacted, 8 KB) request and response bodies in the request log. Off by default: they contain your prompts.</summary>
    public bool LogBodies { get; set; }
    /// <summary>How many recent API requests the request log keeps.</summary>
    public int RequestLogSize { get; set; } = 2000;
}

/// <summary>Strips secrets (bearer tokens, API keys, Hugging Face tokens) from text before it is shown or written.</summary>
public static class LogRedactor
{
    private static readonly Regex[] Patterns =
    {
        new(@"(?i)(bearer\s+)[A-Za-z0-9._~+/=\-]{8,}", RegexOptions.Compiled),
        new(@"(?i)((?:x-api-key|api[_-]?key|authorization|token|password|secret)[""']?\s*[:=]\s*[""']?)(?!bearer\b)[^\s""',}&]{4,}", RegexOptions.Compiled),
        new(@"\b(?:hf_[A-Za-z0-9]{16,}|ait_[A-Za-z0-9_\-]{10,}|sk-[A-Za-z0-9_\-]{16,})", RegexOptions.Compiled),
    };

    public const string Mask = "***";

    public static string Redact(string text)
    {
        if (text.Length < 8) return text;
        text = Patterns[0].Replace(text, "$1" + Mask);
        text = Patterns[1].Replace(text, "$1" + Mask);
        return Patterns[2].Replace(text, Mask);
    }
}

/// <summary>
/// JSON-lines log files under <c>~/.llmhost/logs</c>: <c>llmhost-YYYYMMDD.log</c>, rolled over to
/// <c>llmhost-YYYYMMDD.1.log</c>, <c>.2.log</c>… when a file reaches the size limit; old files are deleted by age.
/// </summary>
public sealed class FileLog
{
    private readonly string _dir;
    private readonly Func<LoggingSettings> _settings;
    private readonly object _gate = new();
    private DateTime _lastCleanup = DateTime.MinValue;

    public FileLog(string dir, Func<LoggingSettings> settings)
    {
        _dir = dir;
        _settings = settings;
    }

    public string Directory => _dir;

    public static string DefaultDirectory => Path.Combine(AppPaths.Root, "logs");

    public void Write(DateTime time, LogLevel level, string source, string message)
    {
        var cfg = _settings();
        var min = LogLevels.Parse(cfg.Level, LogLevel.Info);
        if (min == LogLevel.Off || level < min) return;
        try
        {
            string line = JsonSerializer.Serialize(new { ts = time.ToString("o"), level = LogLevels.Name(level), source, msg = message }, Json.Compact);
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_dir);
                string path = TargetFile(time.Date, Math.Max(1, cfg.MaxFileMb) * 1024L * 1024L, Encoding.UTF8.GetByteCount(line) + 2);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromHours(1)) { _lastCleanup = DateTime.UtcNow; Cleanup(cfg.RetainDays); }
            }
        }
        catch { /* logging must never break the app */ }
    }

    /// <summary>The file for <paramref name="day"/> that still has room; rolls the current one over when it is full.</summary>
    private string TargetFile(DateTime day, long maxBytes, int incoming)
    {
        string stem = Path.Combine(_dir, $"llmhost-{day:yyyyMMdd}");
        string current = stem + ".log";
        if (!File.Exists(current) || new FileInfo(current).Length + incoming <= maxBytes) return current;

        int n = 1;
        while (File.Exists($"{stem}.{n}.log")) n++;
        File.Move(current, $"{stem}.{n}.log");
        return current;
    }

    public void Cleanup(int retainDays)
    {
        if (retainDays <= 0 || !System.IO.Directory.Exists(_dir)) return;
        var cutoff = DateTime.Now.AddDays(-retainDays);
        foreach (var f in System.IO.Directory.EnumerateFiles(_dir, "llmhost-*.log"))
        {
            try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
        }
    }

    /// <summary>Log files, newest first (for "open logs folder" / diagnostic export).</summary>
    public IReadOnlyList<string> Files() =>
        System.IO.Directory.Exists(_dir)
            ? System.IO.Directory.EnumerateFiles(_dir, "llmhost-*.log").OrderByDescending(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();
}
