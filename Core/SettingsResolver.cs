using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMHostProject.Core;

/// <summary>What a chat stores of its own settings: the preset it follows, the fields it changed, and load options kept for the next explicit reload.</summary>
public class ChatSettings
{
    public string? Preset { get; set; }
    /// <summary>Only the generation fields that differ from the preset / model default / global setting the chat starts from (JSON property names).</summary>
    public JsonObject? Overrides { get; set; }
    /// <summary>Shown in the panel, applied only when the user reloads the model.</summary>
    public LoadOptions? Load { get; set; }
}

/// <summary>The settings that apply to one request and where each value came from. <c>Sources</c> maps a field to request | chat | preset | model | global.</summary>
public record EffectiveSettings(GenerationOptions Generation, string BaseLayer, IReadOnlyDictionary<string, string> Sources, JsonObject Json);

/// <summary>
/// The one place that decides which settings win: request override &gt; chat &gt; preset &gt; model default &gt; global. A preset, a model default and the
/// global settings are complete sets (the first one that exists is the base); the chat and the request only carry the fields they change.
/// The page shows the result and the server uses it, so both always agree.
/// </summary>
public static class SettingsResolver
{
    public static EffectiveSettings Effective(JsonObject? chatOverrides, GenerationOptions? preset, GenerationOptions? modelDefault, GenerationOptions global, JsonObject? requestOverride = null)
    {
        string layer = preset != null ? "preset" : modelDefault != null ? "model" : "global";
        var baseJson = ToJson(preset ?? modelDefault ?? global);
        var result = (JsonObject)baseJson.DeepClone();
        var sources = baseJson.ToDictionary(kv => kv.Key, _ => layer);
        void Apply(JsonObject? over, string name)
        {
            if (over == null) return;
            foreach (var kv in over) { result[kv.Key] = kv.Value?.DeepClone(); sources[kv.Key] = name; }
        }
        Apply(chatOverrides, "chat");
        Apply(requestOverride, "request");
        var gen = result.Deserialize<GenerationOptions>(Json.Options) ?? new GenerationOptions();
        return new EffectiveSettings(gen, layer, sources, result);
    }

    public static JsonObject ToJson(GenerationOptions g) => JsonSerializer.SerializeToNode(g, Json.Options)!.AsObject();

    private static readonly HashSet<string> GenerationKeys = typeof(GenerationOptions).GetProperties()
        .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fields of <paramref name="wanted"/> (a complete set of generation settings) whose value differs from <paramref name="baseSet"/>.</summary>
    public static JsonObject Diff(GenerationOptions baseSet, JsonObject wanted)
    {
        var b = ToJson(baseSet);
        var d = new JsonObject();
        foreach (var kv in wanted)
        {
            if (kv.Value == null || !GenerationKeys.Contains(kv.Key)) continue;                 // only real generation settings
            if (!b.TryGetPropertyValue(kv.Key, out var bv) || bv == null || !JsonNode.DeepEquals(NormalizeNumber(bv), NormalizeNumber(kv.Value))) d[kv.Key] = kv.Value.DeepClone();
        }
        return d;
    }

    // 0.7 written by the page and 0.699999988079071 from a float field are the same setting
    private static JsonNode NormalizeNumber(JsonNode n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) ? JsonValue.Create(Math.Round(d, 4))! : n;
}
