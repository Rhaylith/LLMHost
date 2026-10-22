using System.Text.Json;
using LLMHostProject.Core;

namespace LLMHostProject.Cli;

public enum OutputMode { Human, Json, Quiet }

/// <summary>
/// Where command output goes. Human: as before. Json: results are JSON on stdout (one document, or JSON lines for streams) and everything meant
/// for people goes to stderr. Quiet: no informational lines, results and errors only.
/// </summary>
public static class Out
{
    public static OutputMode Mode { get; set; } = OutputMode.Human;
    public static bool Verbose { get; set; }
    /// <summary>The exit code the first reported error asked for (see <see cref="ExitCodes"/>).</summary>
    public static int ExitCode { get; set; }

    private static TextWriter? _out, _err;
    public static TextWriter Stdout { get => _out ?? Console.Out; set => _out = value; }
    public static TextWriter Stderr { get => _err ?? Console.Error; set => _err = value; }

    public static bool IsJson => Mode == OutputMode.Json;

    /// <summary>A line for people.</summary>
    public static void Human(string text)
    {
        if (Mode == OutputMode.Quiet) return;
        (IsJson ? Stderr : Stdout).WriteLine(text);
    }

    /// <summary>The result of a command as JSON (prints nothing in human mode, where the caller printed its own text).</summary>
    public static void Result(object value)
    {
        if (!IsJson) return;
        Stdout.WriteLine(JsonSerializer.Serialize(value, Json.Compact));
    }

    /// <summary>A table: in JSON mode an array of objects keyed by the lower-cased column names.</summary>
    public static void Rows(IReadOnlyList<string[]> rows, string[] header)
    {
        Result(rows.Select(r => header.Select((h, i) => (h.ToLowerInvariant(), i < r.Length ? r[i] : "")).ToDictionary(x => x.Item1, x => x.Item2)).ToList());
    }

    public static void Error(string message, int code = ExitCodes.Generic)
    {
        if (ExitCode == 0) ExitCode = code;
        Stderr.WriteLine(Ui.Red("error: ") + message);
    }

    /// <summary>Reads the global flags (<c>--json</c>, <c>--quiet</c>/<c>-q</c>, <c>--no-color</c>, <c>--verbose</c>) and returns the other arguments. <c>--json</c> stays in the list for verbs that look at it.</summary>
    public static string[] ParseGlobalFlags(string[] args)
    {
        var rest = new List<string>();
        foreach (var a in args)
        {
            switch (a)
            {
                case "--json": Mode = OutputMode.Json; break;
                case "--quiet": case "-q": if (Mode != OutputMode.Json) Mode = OutputMode.Quiet; break;
                case "--no-color": Ui.Color = false; break;
                case "--verbose": Verbose = true; break;
                default: rest.Add(a); break;
            }
        }
        if (IsJson) rest.Add("--json");                    // verbs that have their own JSON look for the flag at the end, wherever it was typed
        return rest.ToArray();
    }

    public static void Reset() { Mode = OutputMode.Human; Verbose = false; ExitCode = 0; _out = null; _err = null; Ui.Color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null; }
}
