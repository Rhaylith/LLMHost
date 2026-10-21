using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LLMHostProject.Core;

public enum MigrationResult { UpToDate, Migrated, NewerThanApp, Invalid, Missing }

/// <summary>
/// Versioned on-disk formats. Every file the app owns (settings, presets, per-model defaults, chats) carries a
/// <c>schemaVersion</c>. Files written before versioning existed count as version 0. To change a format: append one
/// step to <see cref="Steps"/> for that kind (step <c>i</c> upgrades version <c>i</c> to <c>i + 1</c>); the current version
/// is the number of steps. Before a file is rewritten the original is kept as <c>&lt;file&gt;.bak-v&lt;old&gt;</c>.
/// </summary>
public static class Migrations
{
    public const string VersionKey = "schemaVersion";

    public const string Settings = "settings";
    public const string Presets = "presets";
    public const string ModelDefaults = "model-defaults";
    public const string Chat = "chat";

    private static readonly Dictionary<string, List<Func<JsonObject, JsonObject>>> Steps = new()
    {
        // v0 -> v1: version stamp only
        [Settings] = new() { o => o },
        [Chat] = new() { o => o },
        // v0 -> v1: the legacy files were a bare { name: value } dictionary; the version needs a place to live
        [Presets] = new() { WrapItems },
        [ModelDefaults] = new() { WrapItems },
    };

    private static JsonObject WrapItems(JsonObject legacy) => new() { ["items"] = legacy };

    public static int CurrentVersion(string kind) => Steps[kind].Count;

    private static readonly object Gate = new();
    private static bool _ran;

    /// <summary>Upgrades every versioned file under the data folder once per process.</summary>
    public static void RunAll()
    {
        lock (Gate)
        {
            if (_ran) return;
            _ran = true;
            try
            {
                MigrateFile(AppPaths.SettingsFile, Settings);
                MigrateFile(AppPaths.PresetsFile, Presets);
                MigrateFile(AppPaths.ModelDefaultsFile, ModelDefaults);
                if (Directory.Exists(AppPaths.ChatsDir))
                    foreach (var f in Directory.EnumerateFiles(AppPaths.ChatsDir, "*.json"))
                        MigrateFile(f, Chat);
            }
            catch (Exception ex)
            {
                LogBuffer.Add("settings", $"Migration failed: {ex.Message}");
            }
        }
    }

    /// <summary>Version stored in a document (0 when it has none).</summary>
    public static int VersionOf(JsonObject o) =>
        o[VersionKey] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    /// <summary>Upgrades a document in memory. Returns the new document, or null when it is newer than this app understands.</summary>
    public static JsonObject? Upgrade(string kind, JsonObject doc, out int from)
    {
        var steps = Steps[kind];
        from = VersionOf(doc);
        if (from > steps.Count) return null;
        for (int v = from; v < steps.Count; v++) doc = steps[v](doc);
        doc[VersionKey] = steps.Count;
        return doc;
    }

    public static MigrationResult MigrateFile(string path, string kind)
    {
        if (!File.Exists(path)) return MigrationResult.Missing;
        string text = File.ReadAllText(path);
        JsonObject? doc;
        try { doc = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { doc = null; }
        if (doc == null)
        {
            Keep(path, ".invalid", text);
            LogBuffer.Add("settings", $"{Path.GetFileName(path)} is not a valid JSON object; a copy was kept as {Path.GetFileName(path)}.invalid.");
            return MigrationResult.Invalid;
        }

        var upgraded = Upgrade(kind, doc, out int from);
        if (upgraded == null)
        {
            Keep(path, $".bak-v{from}", text);
            LogBuffer.Add("settings", $"{Path.GetFileName(path)} was written by a newer version (schema {from} > {CurrentVersion(kind)}); a backup was kept and unknown data may be lost.");
            return MigrationResult.NewerThanApp;
        }
        if (from == CurrentVersion(kind)) return MigrationResult.UpToDate;

        Keep(path, $".bak-v{from}", text);
        WriteAtomic(path, upgraded.ToJsonString(Json.Options));
        LogBuffer.Add("settings", $"Upgraded {Path.GetFileName(path)} from schema {from} to {CurrentVersion(kind)}.");
        return MigrationResult.Migrated;
    }

    /// <summary>Writes a copy of <paramref name="text"/> next to <paramref name="path"/> unless one already exists.</summary>
    private static void Keep(string path, string suffix, string text)
    {
        try
        {
            string target = path + suffix;
            if (!File.Exists(target)) File.WriteAllText(target, text);
        }
        catch { /* a read-only folder must not stop start-up */ }
    }

    public static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    // ------------------------------------------------------------ { schemaVersion, items: { ... } } files

    public static Dictionary<string, T> ReadItems<T>(string path, string kind)
    {
        try
        {
            if (!File.Exists(path)) return new();
            MigrateFile(path, kind);
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject o && o["items"] is JsonObject items)
                return items.Deserialize<Dictionary<string, T>>(Json.Options) ?? new();
        }
        catch { /* corrupt file: start empty */ }
        return new();
    }

    public static void WriteItems<T>(string path, string kind, Dictionary<string, T> items)
    {
        AppPaths.EnsureCreated();
        var doc = new JsonObject
        {
            [VersionKey] = CurrentVersion(kind),
            ["items"] = JsonSerializer.SerializeToNode(items, Json.Options),
        };
        WriteAtomic(path, doc.ToJsonString(Json.Options));
    }

    // ------------------------------------------------------------ unknown keys

    /// <summary>
    /// Property paths in <paramref name="node"/> (e.g. <c>defaultLoad.fooBar</c>) that <paramref name="type"/> does not have.
    /// Such keys are dropped on the next save, so callers keep them aside (see <c>settings.json.invalid</c>).
    /// </summary>
    public static Dictionary<string, JsonNode?> FindUnknownKeys(JsonNode? node, Type type, string prefix = "")
    {
        var found = new Dictionary<string, JsonNode?>();
        if (node is not JsonObject obj) return found;
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in obj)
        {
            if (!props.TryGetValue(key, out var p)) { found[prefix + key] = value?.DeepClone(); continue; }
            if (p.PropertyType.IsClass && p.PropertyType.Namespace == type.Namespace && p.PropertyType != typeof(string))
                foreach (var kv in FindUnknownKeys(value, p.PropertyType, prefix + key + "."))
                    found[kv.Key] = kv.Value;
        }
        return found;
    }
}
