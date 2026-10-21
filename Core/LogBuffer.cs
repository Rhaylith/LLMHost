namespace LLMHostProject.Core;

/// <summary>Thread-safe ring buffer of log lines (native llama.cpp output + app events).</summary>
public static class LogBuffer
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> Lines = new();
    private const int Max = 2000;

    public static event Action<string>? Written;

    public static void Add(string source, string message)
    {
        message = message.TrimEnd('\r', '\n');
        if (message.Length == 0) return;
        var line = $"{DateTime.Now:HH:mm:ss} [{source}] {message}";
        lock (Gate)
        {
            Lines.AddLast(line);
            while (Lines.Count > Max) Lines.RemoveFirst();
        }
        Written?.Invoke(line);
    }

    public static string[] Tail(int count = 200)
    {
        lock (Gate) return Lines.Skip(Math.Max(0, Lines.Count - count)).ToArray();
    }
}
