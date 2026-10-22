using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;

namespace LLMHostProject.Server;

/// <summary>The <c>thinking</c> request parameter, and the signature clients echo back on thinking blocks.</summary>
public static class AnthropicThinking
{
    /// <summary>
    /// <c>thinking: {type: "enabled", budget_tokens: N}</c> sets the reasoning effort from the budget (up to 1024 low, up to 8192 medium, above that high)
    /// and the hard limit of thinking tokens; <c>{type: "disabled"}</c> switches reasoning off. Without the parameter nothing changes.
    /// </summary>
    public static void Apply(JsonNode body, GenerationOptions g)
    {
        if (body["thinking"] is not JsonObject t) return;
        string type = t["type"]?.GetValue<string>() ?? "";
        if (type == "disabled") { g.ReasoningEffort = "off"; return; }
        if (type != "enabled") return;
        int budget = t["budget_tokens"] is JsonValue v && v.TryGetValue<int>(out var b) ? b : 0;
        g.ReasoningEffort = budget <= 0 ? "medium" : budget <= 1024 ? "low" : budget <= 8192 ? "medium" : "high";
        if (budget > 0) g.ThinkBudgetTokens = budget;
    }

    private static byte[]? _key;
    private static byte[] Key
    {
        get
        {
            if (_key != null) return _key;
            string path = Path.Combine(AppPaths.Root, "anthropic-signing.key");
            try
            {
                if (File.Exists(path)) _key = Convert.FromBase64String(File.ReadAllText(path).Trim());
                else { _key = RandomNumberGenerator.GetBytes(32); AppPaths.EnsureCreated(); File.WriteAllText(path, Convert.ToBase64String(_key)); }
            }
            catch { _key ??= RandomNumberGenerator.GetBytes(32); }
            return _key;
        }
    }

    /// <summary>HMAC of the thinking text, as clients expect a <c>signature</c> they can send back unchanged.</summary>
    public static string Sign(string thinking) => Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(thinking)));

    public static bool Verify(string thinking, string? signature) =>
        !string.IsNullOrEmpty(signature) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(thinking)), Encoding.ASCII.GetBytes(signature));

    /// <summary>Whether the request asks for prompt caching anywhere (<c>cache_control</c> on the system prompt, a message block or a tool).</summary>
    public static bool WantsCache(JsonNode body) => body.ToJsonString().Contains("\"cache_control\"", StringComparison.Ordinal);
}
