using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMHostProject.Core;

/// <summary>
/// One preset as it is stored in <c>presets/&lt;name&gt;.json</c>: <c>{ schemaVersion, name, description, systemPrompt, inference: {…} }</c>.
/// Only inference settings belong in a preset; how a model is loaded (context length, GPU layers …) never does.
/// </summary>
public class PresetFile
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? SystemPrompt { get; set; }
    public JsonObject Inference { get; set; } = new();

    public GenerationOptions ToGeneration()
    {
        var o = (JsonObject)Inference.DeepClone();
        if (SystemPrompt != null) o["systemPrompt"] = SystemPrompt;
        return o.Deserialize<GenerationOptions>(Json.Options) ?? new GenerationOptions();
    }

    public static PresetFile From(string name, GenerationOptions g, string? description = null)
    {
        var inf = SettingsResolver.ToJson(g);
        inf.Remove("systemPrompt");
        return new PresetFile { Name = name, Description = description, SystemPrompt = g.SystemPrompt, Inference = inf };
    }
}

/// <summary>Result of reading a preset document: the preset and what was dropped or not understood.</summary>
public record PresetCheck(PresetFile? Preset, IReadOnlyList<string> Dropped, IReadOnlyList<string> Unknown, string? Error);

public static class PresetRules
{
    private static readonly HashSet<string> GenerationKeys = typeof(GenerationOptions).GetProperties()
        .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys that only make sense when a model is loaded (the properties of <see cref="LoadOptions"/> that generation settings do not have).</summary>
    private static readonly HashSet<string> LoadKeys = typeof(LoadOptions).GetProperties()
        .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Where(k => !GenerationKeys.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "LPT1", "LPT2", "LPT3" };

    /// <summary>A name that is safe as a file name: letters, digits, spaces, dots, dashes, underscores; null when nothing usable is left.</summary>
    public static string? SafeName(string? name)
    {
        string n = Regex.Replace((name ?? "").Trim(), @"[^\p{L}\p{N} ._()-]+", "-");
        n = Regex.Replace(n, @"\s+", " ").Trim(' ', '.', '-');
        if (n.Length == 0) return null;
        if (n.Length > 60) n = n[..60].TrimEnd(' ', '.', '-');
        if (Reserved.Contains(n.Split('.')[0], StringComparer.OrdinalIgnoreCase)) n += "-preset";
        return n;
    }

    /// <summary>Reads a preset document. <c>load</c> and every load-only key are dropped (and reported), unknown keys are reported and ignored.</summary>
    public static PresetCheck Check(string json, string? fallbackName = null)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch (JsonException ex) { return new(null, Array.Empty<string>(), Array.Empty<string>(), "Not valid JSON: " + ex.Message); }
        if (node is not JsonObject o) return new(null, Array.Empty<string>(), Array.Empty<string>(), "A preset is a JSON object.");
        var dropped = new List<string>(); var unknown = new List<string>();
        if (o.ContainsKey("load")) dropped.Add("load");
        var inf = new JsonObject();
        foreach (var kv in o["inference"] as JsonObject ?? new JsonObject())
        {
            if (kv.Key.Equals("systemPrompt", StringComparison.OrdinalIgnoreCase)) continue;      // lives at the top level
            if (LoadKeys.Contains(kv.Key)) dropped.Add("inference." + kv.Key);
            else if (!GenerationKeys.Contains(kv.Key)) unknown.Add("inference." + kv.Key);
            else if (kv.Value != null) inf[JsonNamingPolicy.CamelCase.ConvertName(kv.Key)] = kv.Value.DeepClone();
        }
        string? name = SafeName(o["name"]?.GetValue<string>() ?? fallbackName);
        if (name == null) return new(null, dropped, unknown, "The preset needs a name.");
        var preset = new PresetFile
        {
            Name = name, Description = o["description"]?.GetValue<string>(), SystemPrompt = o["systemPrompt"]?.GetValue<string>(), Inference = inf,
        };
        // the values must fit the settings class (a string where a number belongs, ...)
        try { preset.ToGeneration(); } catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return new(null, dropped, unknown, "A value has the wrong type: " + ex.Message); }
        return new(preset, dropped, unknown, null);
    }
}

/// <summary>
/// Presets as one file each in <c>~/.llmhost/presets</c>, so they can be edited, shared and version-controlled by hand. The folder is watched:
/// a change on disk bumps <see cref="Version"/>, which the page polls to refresh its list.
/// </summary>
public class PresetStore
{
    private readonly object _gate = new();
    private readonly string _dir;
    private FileSystemWatcher? _watcher;
    private int _version;

    /// <summary>Changes whenever a preset file is added, edited or removed (by this app or by hand).</summary>
    public int Version => Volatile.Read(ref _version);

    public PresetStore() : this(AppPaths.PresetsDir) { }

    public PresetStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
        MigrateOldFile();
        try
        {
            _watcher = new FileSystemWatcher(_dir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
            FileSystemEventHandler bump = (_, _) => Interlocked.Increment(ref _version);
            _watcher.Created += bump; _watcher.Changed += bump; _watcher.Deleted += bump; _watcher.Renamed += (_, _) => Interlocked.Increment(ref _version);
        }
        catch { /* watching is a convenience; the list is read from disk on every request anyway */ }
    }

    /// <summary>The single presets.json of earlier versions becomes one file per preset; the old file is kept as presets.json.migrated.</summary>
    private void MigrateOldFile()
    {
        string old = AppPaths.PresetsFile;
        if (!File.Exists(old) || !string.Equals(Path.GetFullPath(_dir), Path.GetFullPath(AppPaths.PresetsDir), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Migrations.MigrateFile(old, Migrations.Presets);
            foreach (var (name, g) in Migrations.ReadItems<GenerationOptions>(old, Migrations.Presets))
            {
                string? safe = PresetRules.SafeName(name);
                if (safe == null || File.Exists(PathFor(safe))) continue;
                Write(PresetFile.From(safe, g));
            }
            File.Move(old, old + ".migrated", true);
            LogBuffer.Add("settings", "Presets moved from presets.json to one file each in the presets folder.");
        }
        catch (Exception ex) { LogBuffer.Add("settings", "Could not migrate presets.json: " + ex.Message); }
    }

    private string PathFor(string safeName) => Path.Combine(_dir, safeName + ".json");

    private void Write(PresetFile p) => Migrations.WriteAtomic(PathFor(PresetRules.SafeName(p.Name)!), JsonSerializer.Serialize(p, Json.Options));

    /// <summary>All valid preset files, by name. Files that cannot be read are skipped (and logged).</summary>
    public List<PresetFile> Files()
    {
        lock (_gate)
        {
            var list = new List<PresetFile>();
            foreach (var f in Directory.EnumerateFiles(_dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var r = PresetRules.Check(File.ReadAllText(f), Path.GetFileNameWithoutExtension(f));
                if (r.Preset == null) { LogBuffer.Add("settings", $"Preset file {Path.GetFileName(f)} ignored: {r.Error}"); continue; }
                list.Add(r.Preset);
            }
            return list;
        }
    }

    public Dictionary<string, GenerationOptions> All() => Files().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.First().Name, g => g.First().ToGeneration(), StringComparer.OrdinalIgnoreCase);

    public PresetFile? Get(string name) => Files().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates or replaces a preset. The name is made safe for use as a file name; the stored name is returned.</summary>
    public string Save(string name, GenerationOptions opts, string? description = null)
    {
        string safe = PresetRules.SafeName(name) ?? throw new ArgumentException("A preset needs a name (letters, digits, spaces, . - _).");
        lock (_gate)
        {
            description ??= Get(safe)?.Description;
            Write(PresetFile.From(safe, opts, description));
        }
        return safe;
    }

    /// <summary>Saves a document that was checked with <see cref="PresetRules.Check"/> (import).</summary>
    public string Save(PresetFile p) { lock (_gate) { Write(p); } return p.Name; }

    public bool Delete(string name)
    {
        lock (_gate)
        {
            var file = Files().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (file == null) return false;                                    // only a preset that really exists is touched
            string path = PathFor(PresetRules.SafeName(file.Name)!);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }

    /// <summary>Copies a preset under a new name ("name (copy)", "name (copy 2)" ... when none is given).</summary>
    public string Duplicate(string name, string? newName = null)
    {
        var src = Get(name) ?? throw new FileNotFoundException($"No preset named '{name}'.");
        string target;
        if (!string.IsNullOrWhiteSpace(newName)) target = PresetRules.SafeName(newName) ?? throw new ArgumentException("Invalid name.");
        else
        {
            target = PresetRules.SafeName(src.Name + " (copy)")!;
            for (int i = 2; Get(target) != null; i++) target = PresetRules.SafeName($"{src.Name} (copy {i})")!;
        }
        if (Get(target) != null) throw new InvalidOperationException($"A preset named '{target}' already exists.");
        lock (_gate) Write(new PresetFile { Name = target, Description = src.Description, SystemPrompt = src.SystemPrompt, Inference = (JsonObject)src.Inference.DeepClone() });
        return target;
    }
}
