using System.Text;
using LLMHostProject.Core;

namespace LLMHostProject.Cli;

/// <summary>Small console helpers: colours, tables, progress.</summary>
public static class Ui
{
    private static readonly bool Color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null;

    public static string C(string text, string code) => Color ? $"\u001b[{code}m{text}\u001b[0m" : text;
    public static string Dim(string t) => C(t, "90");
    public static string Bold(string t) => C(t, "1");
    public static string Green(string t) => C(t, "32");
    public static string Red(string t) => C(t, "31");
    public static string Cyan(string t) => C(t, "36");
    public static string Yellow(string t) => C(t, "33");

    public static void Info(string t) => Console.WriteLine(t);
    public static void Error(string t) => Console.WriteLine(Red("error: ") + t);

    public static void EnableAnsi()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Console.OutputEncoding = Encoding.UTF8; Console.InputEncoding = Encoding.UTF8; } catch { }
    }

    public static void Table(IEnumerable<string[]> rows, params string[] header)
    {
        var all = new List<string[]> { header };
        all.AddRange(rows);
        var widths = new int[header.Length];
        foreach (var r in all)
            for (int i = 0; i < header.Length && i < r.Length; i++)
                widths[i] = Math.Max(widths[i], r[i].Length);
        for (int ri = 0; ri < all.Count; ri++)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < header.Length; i++)
            {
                string cell = i < all[ri].Length ? all[ri][i] : "";
                sb.Append(cell.PadRight(widths[i] + 2));
            }
            Console.WriteLine(ri == 0 ? Bold(sb.ToString().TrimEnd()) : sb.ToString().TrimEnd());
        }
    }

    public static string Bytes(long b) => Hardware.FormatBytes(b);

    public static string Bar(double fraction, int width = 30)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        int fill = (int)(fraction * width);
        return "[" + new string('#', fill) + new string('.', width - fill) + "]";
    }
}
