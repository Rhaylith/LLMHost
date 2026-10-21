namespace LLMHostProject.Core;

/// <summary>
/// Thread-safe ring buffer of log lines (native llama.cpp output + app events) that also feeds the rotating
/// JSON-lines log files (<see cref="FileLog"/>). Secrets are redacted before anything is stored.
/// </summary>
public static class LogBuffer
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Lines = new();
    private const int Max = 2000;
    private static FileLog? _file;

    public static event Action<string>? Written;

    /// <summary>The file sink (created on first use under the data folder).</summary>
    public static FileLog Sink => _file ??= new FileLog(FileLog.DefaultDirectory, () => AppSettings.Loaded?.Logging ?? new LoggingSettings());

    public static void Add(string source, string message, LogLevel level = LogLevel.Info)
    {
        message = LogRedactor.Redact(message.TrimEnd('\r', '\n'));
        if (message.Length == 0) return;
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss} [{source}] {message}";
        lock (Gate)
        {
            Lines.AddLast(line);
            while (Lines.Count > Max) Lines.RemoveFirst();
        }
        Sink.Write(now, level, source, message);
        Written?.Invoke(line);
    }

    public static string[] Tail(int count = 200)
    {
        lock (Gate) return Lines.Skip(Math.Max(0, Lines.Count - count)).ToArray();
    }
}
