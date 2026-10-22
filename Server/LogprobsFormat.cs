using System.Text.Json.Nodes;
using LLMHostProject.Inference;

namespace LLMHostProject.Server;

/// <summary>
/// The answer text starts after the whitespace the model produced at the beginning has been dropped. This does the same to the
/// log-probabilities so that the bytes of all tokens spell exactly the text that was returned. Keep one instance per stream.
/// </summary>
public sealed class LeadingTrim
{
    private bool _done;

    public List<TokenLogprob> Apply(IEnumerable<TokenLogprob> items)
    {
        var result = new List<TokenLogprob>();
        foreach (var t in items)
        {
            if (t.Reasoning || _done) { result.Add(t); continue; }
            int skip = 0;
            while (skip < t.Bytes.Length && (t.Bytes[skip] is 0x20 or 0x0A or 0x0D)) skip++;
            if (skip == t.Bytes.Length) continue;                         // nothing but whitespace: dropped from the text, so from here too
            _done = true;
            if (skip == 0) { result.Add(t); continue; }
            var bytes = t.Bytes[skip..];
            result.Add(t with { Token = System.Text.Encoding.UTF8.GetString(bytes), Bytes = bytes });
        }
        return result;
    }
}

/// <summary>Log-probabilities in the shapes the different APIs use.</summary>
public static class LogprobsFormat
{
    /// <summary>Largest <c>top_logprobs</c> OpenAI accepts; larger requests are clamped to it.</summary>
    public const int MaxTop = 20;

    private static List<int> Ints(byte[] b) => b.Select(x => (int)x).ToList();

    private static List<TokenLogprob> Answer(IEnumerable<TokenLogprob> items, LeadingTrim? trim) =>
        (trim ?? new LeadingTrim()).Apply(items).Where(t => !t.Reasoning).ToList();

    /// <summary>Chat completions: <c>{content: [{token, logprob, bytes, top_logprobs: [...]}], refusal: null}</c> (reasoning tokens left out).</summary>
    public static object? Chat(IEnumerable<TokenLogprob> items, LeadingTrim? trim = null)
    {
        var list = Answer(items, trim);
        if (list.Count == 0) return null;
        return new
        {
            content = list.Select(t => new
            {
                token = t.Token, logprob = t.Logprob, bytes = Ints(t.Bytes),
                top_logprobs = t.Top.Select(x => new { token = x.Token, logprob = x.Logprob, bytes = Ints(x.Bytes) }).ToList(),
            }).ToList(),
            refusal = (object?)null,
        };
    }

    /// <summary>Legacy completions: <c>{tokens, token_logprobs, top_logprobs: [{token: logprob}], text_offset}</c>.</summary>
    public static object? Text(IEnumerable<TokenLogprob> items, LeadingTrim? trim = null)
    {
        var list = Answer(items, trim);
        if (list.Count == 0) return null;
        int off = 0;
        var offsets = new List<int>();
        foreach (var t in list) { offsets.Add(off); off += t.Token.Length; }
        return new
        {
            tokens = list.Select(t => t.Token).ToList(),
            token_logprobs = list.Select(t => t.Logprob).ToList(),
            top_logprobs = list.Select(t => t.Top.GroupBy(x => x.Token).ToDictionary(x => x.Key, x => x.First().Logprob)).ToList(),
            text_offset = offsets,
        };
    }

    /// <summary>Responses API / native API: an array of <c>{token, logprob, bytes, top_logprobs: [{token, logprob, bytes}]}</c>.</summary>
    public static JsonArray Items(IEnumerable<TokenLogprob> items, LeadingTrim? trim = null)
    {
        var arr = new JsonArray();
        foreach (var t in Answer(items, trim))
        {
            var top = new JsonArray();
            foreach (var x in t.Top)
                top.Add(new JsonObject { ["token"] = x.Token, ["logprob"] = x.Logprob, ["bytes"] = new JsonArray(x.Bytes.Select(b => (JsonNode?)JsonValue.Create((int)b)).ToArray()) });
            arr.Add(new JsonObject
            {
                ["token"] = t.Token, ["logprob"] = t.Logprob,
                ["bytes"] = new JsonArray(t.Bytes.Select(b => (JsonNode?)JsonValue.Create((int)b)).ToArray()),
                ["top_logprobs"] = top,
            });
        }
        return arr;
    }
}
