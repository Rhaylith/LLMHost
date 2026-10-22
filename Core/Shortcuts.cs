using System.Text.RegularExpressions;

namespace LLMHostProject.Core;

/// <summary>One thing a key combination can do in the web UI.</summary>
/// <param name="Keys">Default combination(s), the first one is shown; "" = none.</param>
public record ShortcutAction(string Id, string Label, string Keys, string Group);

/// <summary>
/// The keyboard shortcuts of the web UI: the actions with their default keys, the combination syntax, and the check that no two
/// actions share a combination. The page fetches the effective table from <c>/api/shortcuts</c> and binds the keys itself.
/// </summary>
public static class Shortcuts
{
    public static readonly IReadOnlyList<ShortcutAction> Defaults = new ShortcutAction[]
    {
        new("newChat", "New chat", "Ctrl+N", "Chats"),
        new("newFolder", "New chat folder", "Ctrl+Shift+N", "Chats"),
        new("quickOpen", "Quick open (chats, models, commands)", "Ctrl+K", "Navigation"),
        new("quickOpen2", "Quick open (second key)", "Ctrl+P", "Navigation"),
        new("settings", "Open Server & Settings", "Ctrl+,", "Navigation"),
        new("runtimes", "Runtime manager", "Ctrl+Shift+R", "Navigation"),
        new("help", "Show the shortcut list", "?", "Navigation"),
        new("focusInput", "Focus the message box", "Ctrl+L", "Chat"),
        new("send", "Send the message", "Ctrl+Enter", "Chat"),
        new("stop", "Stop generating", "Escape", "Chat"),
        new("copyLast", "Copy the last answer", "Ctrl+Shift+C", "Chat"),
        new("prevVariant", "Previous variant of the last answer", "Alt+ArrowLeft", "Chat"),
        new("nextVariant", "Next variant of the last answer", "Alt+ArrowRight", "Chat"),
    };

    private static readonly string[] Named =
    {
        "Enter", "Escape", "Tab", "Space", "Backspace", "Delete", "Home", "End", "PageUp", "PageDown", "Insert",
        "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown",
    };

    private static readonly Regex Combo = new(@"^(?:(?<m>Ctrl|Alt|Shift|Meta)\+)*(?<key>[^\s+]|\+|F([1-9]|1[0-2])|" + string.Join("|", Named) + ")$", RegexOptions.Compiled);

    /// <summary>Whether <paramref name="combo"/> is written like <c>Ctrl+Shift+N</c>, <c>Alt+ArrowLeft</c>, <c>F2</c>, <c>?</c> (modifiers in any order, each once).</summary>
    public static bool IsValid(string? combo)
    {
        if (string.IsNullOrEmpty(combo)) return false;
        var m = Combo.Match(combo);
        if (!m.Success) return false;
        var mods = m.Groups["m"].Captures.Select(c => c.Value).ToList();
        return mods.Distinct().Count() == mods.Count;
    }

    /// <summary>The same combination written in one fixed order (Ctrl, Alt, Shift, Meta, key), single letters upper-case.</summary>
    public static string Normalize(string combo)
    {
        string key, modPart;
        if (combo == "+") { key = "+"; modPart = ""; }
        else if (combo.EndsWith("++")) { key = "+"; modPart = combo[..^2]; }
        else { int i = combo.LastIndexOf('+'); (key, modPart) = i < 0 ? (combo, "") : (combo[(i + 1)..], combo[..i]); }
        var mods = modPart.Length == 0 ? new HashSet<string>() : modPart.Split('+').ToHashSet();
        var ordered = new[] { "Ctrl", "Alt", "Shift", "Meta" }.Where(mods.Contains);
        if (key.Length == 1 && char.IsLetter(key[0])) key = key.ToUpperInvariant();
        return string.Join("+", ordered.Append(key));
    }

    /// <summary>Default keys with the user's choices on top. An empty override removes the shortcut of that action.</summary>
    public static IReadOnlyDictionary<string, string> Effective(IReadOnlyDictionary<string, string>? overrides)
    {
        var map = new Dictionary<string, string>();
        foreach (var a in Defaults)
        {
            string keys = overrides != null && overrides.TryGetValue(a.Id, out var o) ? o : a.Keys;
            if (!string.IsNullOrEmpty(keys)) map[a.Id] = IsValid(keys) ? Normalize(keys) : a.Keys;
        }
        return map;
    }

    /// <summary>Problems in a set of overrides: unknown actions, malformed combinations, two actions on the same keys.</summary>
    public static IReadOnlyList<(string Field, string Message)> Check(IReadOnlyDictionary<string, string>? overrides)
    {
        var problems = new List<(string, string)>();
        if (overrides == null) return problems;
        var ids = Defaults.Select(a => a.Id).ToHashSet();
        foreach (var (id, keys) in overrides)
        {
            if (!ids.Contains(id)) problems.Add(($"shortcuts.{id}", "Unknown action."));
            else if (!string.IsNullOrEmpty(keys) && !IsValid(keys)) problems.Add(($"shortcuts.{id}", $"'{keys}' is not a valid key combination (for example Ctrl+Shift+N)."));
        }
        if (problems.Count > 0) return problems;
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, keys) in Effective(overrides))
        {
            if (used.TryGetValue(keys, out var other))
                problems.Add(($"shortcuts.{id}", $"{keys} is already used by '{Defaults.First(a => a.Id == other).Label}'."));
            else used[keys] = id;
        }
        return problems;
    }
}
