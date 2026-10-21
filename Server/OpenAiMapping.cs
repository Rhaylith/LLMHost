using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;

namespace LLMHostProject.Server;

/// <summary>Translates OpenAI-style request bodies into internal option / message objects.</summary>
public static class OpenAiMapping
{
    public static GenerationOptions ReadGeneration(JsonNode body, GenerationOptions defaults)
    {
        var g = defaults.Clone();
        if (Num(body, "temperature") is { } t) g.Temperature = (float)t;
        if (Num(body, "top_p") is { } tp) g.TopP = (float)tp;
        if (Num(body, "top_k") is { } tk) g.TopK = (int)tk;
        if (Num(body, "min_p") is { } mp) g.MinP = (float)mp;
        if (Num(body, "typical_p") is { } ty) g.TypicalP = (float)ty;
        if (Num(body, "repeat_penalty") is { } rp) g.RepeatPenalty = (float)rp;
        if (Num(body, "repeat_last_n") is { } rl) g.RepeatLastN = (int)rl;
        if (Num(body, "frequency_penalty") is { } fp) g.FrequencyPenalty = (float)fp;
        if (Num(body, "presence_penalty") is { } pp) g.PresencePenalty = (float)pp;
        if (Num(body, "seed") is { } sd && sd >= 0) g.Seed = (uint)sd;
        var max = Num(body, "max_completion_tokens") ?? Num(body, "max_output_tokens") ?? Num(body, "max_tokens") ?? Num(body, "n_predict");
        if (max is { } m) g.MaxTokens = (int)m;

        if (body["stop"] is JsonNode stop)
        {
            g.Stop = new List<string>();
            if (stop is JsonValue sv && sv.TryGetValue<string>(out var s1)) g.Stop.Add(s1);
            else if (stop is JsonArray arr) foreach (var x in arr) if (x?.GetValue<string>() is { } s) g.Stop.Add(s);
        }
        ReadSamplers(body, g);
        ReadReasoning(body, g);
        if (body["grammar"]?.GetValue<string>() is { Length: > 0 } gr) g.Grammar = gr;
        if (body["cache_prompt"] is JsonValue cp && cp.TryGetValue<bool>(out var cache)) g.CachePrompt = cache;
        if (body["logit_bias"] is JsonObject lb)
        {
            g.LogitBias = new Dictionary<int, float>();
            foreach (var (k, v) in lb)
                if (int.TryParse(k, out var tok) && v != null) g.LogitBias[tok] = v.GetValue<float>();
        }
        // logprobs: chat {logprobs: true, top_logprobs: n}, legacy completions {logprobs: n}
        if (body["logprobs"] is JsonValue lp)
        {
            if (lp.TryGetValue<bool>(out var lpb)) { g.Logprobs = lpb; g.TopLogprobs = (int)(Num(body, "top_logprobs") ?? 0); }
            else if (lp.TryGetValue<int>(out var lpn)) { g.Logprobs = lpn >= 0; g.TopLogprobs = lpn; }
        }
        // Responses API: text.format = {type: json_schema | json_object, schema}
        if (body["text"]?["format"] is JsonObject tf)
        {
            switch (tf["type"]?.GetValue<string>())
            {
                case "json_object": g.JsonMode = true; break;
                case "json_schema": if (tf["schema"] != null) g.JsonSchema = tf["schema"]!.ToJsonString(); break;
            }
        }
        if (body["response_format"] is JsonObject rf)
        {
            switch (rf["type"]?.GetValue<string>())
            {
                case "json_object": g.JsonMode = true; break;
                case "json_schema":
                    var schema = rf["json_schema"]?["schema"] ?? rf["schema"];
                    if (schema != null) g.JsonSchema = schema.ToJsonString();
                    break;
            }
        }
        return g;
    }

    /// <summary>
    /// Advanced samplers, using llama-server's field names: mirostat, mirostat_tau, mirostat_eta, dry_multiplier, dry_base,
    /// dry_allowed_length, dry_penalty_last_n, dry_sequence_breakers, xtc_probability, xtc_threshold, top_n_sigma, samplers.
    /// </summary>
    public static void ReadSamplers(JsonNode body, GenerationOptions g)
    {
        if (Num(body, "mirostat") is { } mi) g.Mirostat = (int)mi;
        if (Num(body, "mirostat_tau") is { } mt) g.MirostatTau = (float)mt;
        if (Num(body, "mirostat_eta") is { } me) g.MirostatEta = (float)me;
        if (Num(body, "dry_multiplier") is { } dm) g.DryMultiplier = (float)dm;
        if (Num(body, "dry_base") is { } db) g.DryBase = (float)db;
        if (Num(body, "dry_allowed_length") is { } dl) g.DryAllowedLength = (int)dl;
        if (Num(body, "dry_penalty_last_n") is { } dn) g.DryPenaltyLastN = (int)dn;
        if (Num(body, "xtc_probability") is { } xp) g.XtcProbability = (float)xp;
        if (Num(body, "xtc_threshold") is { } xt) g.XtcThreshold = (float)xt;
        if (Num(body, "top_n_sigma") is { } ns) g.TopNSigma = (float)ns;
        if (body["dry_sequence_breakers"] is JsonArray br)
            g.DrySequenceBreakers = br.Select(x => x?.GetValue<string>()).OfType<string>().ToList();
        if (body["samplers"] is JsonArray so)
            g.SamplerOrder = so.Select(x => x?.GetValue<string>()).OfType<string>().ToList();
    }

    /// <summary>
    /// Reasoning controls from the various dialects: OpenAI <c>reasoning_effort</c> / <c>reasoning.effort</c>,
    /// <c>enable_thinking</c> / <c>think</c> booleans, and Anthropic <c>thinking</c> ({type, budget_tokens}).
    /// </summary>
    public static void ReadReasoning(JsonNode body, GenerationOptions g)
    {
        string? eff = (body["reasoning_effort"] as JsonValue)?.GetValue<string>()
                      ?? (body["reasoning"] as JsonObject)?["effort"]?.GetValue<string>();
        if (eff != null && Reasoning.NormalizeEffort(eff) is { } n) g.ReasoningEffort = n;
        foreach (var key in new[] { "enable_thinking", "think" })
            if (body[key] is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) g.ReasoningEffort = b ? "on" : "off";
                else if (v.TryGetValue<string>(out var s) && Reasoning.NormalizeEffort(s) is { } e2) g.ReasoningEffort = e2;
            }
        if (body["thinking"] is JsonObject th)
        {
            string? type = th["type"]?.GetValue<string>();
            if (type == "disabled") g.ReasoningEffort = "off";
            else if (type == "adaptive") g.ReasoningEffort = "on";
            else if (type == "enabled")
            {
                int budget = th["budget_tokens"] is JsonValue bv && bv.TryGetValue<int>(out var bt) ? bt : 0;
                g.ReasoningEffort = budget <= 0 ? "on" : budget < 2048 ? "low" : budget < 8192 ? "medium" : "high";
            }
        }
    }

    public static double? Num(JsonNode body, string name)
    {
        var n = body[name];
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, out var p)) return p;
        return null;
    }


    private static JsonNode? ParseArgs(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            try { return JsonNode.Parse(s); } catch { return JsonValue.Create(s); }
        }
        return n?.DeepClone();
    }

    private static string ContentText(JsonNode? c, List<byte[]>? images = null)
    {
        if (c == null) return "";
        if (c is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (c is JsonArray parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (p is not JsonObject po) continue;
                switch (po["type"]?.GetValue<string>() ?? "text")
                {
                    case "text": case "input_text":
                        sb.Append(po["text"]?.GetValue<string>());
                        break;
                    case "image_url": case "input_image":
                        // {"type":"image_url","image_url":{"url":"data:..."}} or {"image_url":"data:..."}
                        var iu = po["image_url"];
                        string? url = iu is JsonObject io ? io["url"]?.GetValue<string>() : iu?.GetValue<string>();
                        if (!string.IsNullOrEmpty(url) && images != null) images.Add(ImageInput.FromUrl(url));
                        break;
                }
            }
            return sb.ToString();
        }
        return c.ToJsonString();
    }

    // ---------------------------------------------------------------- tools

    /// <summary>Everything a request says about tools, resolved against the model's tool-call format.</summary>
    public record ToolSetup(ToolFormat Format, List<JsonObject> Functions, string Choice, bool Parallel, string SystemPrompt)
    {
        /// <summary>Name of the single function the model must call, when tool_choice names one.</summary>
        public string? ForcedName => Choice is "auto" or "required" ? null : Choice;
        public bool MustCall => Choice != "auto";
    }

    private static ToolSetup? Build(ToolFormat fmt, JsonArray tools, string choice, bool parallel)
    {
        if (choice == "none") return null;
        var fns = ToolFormats.Functions(tools);
        if (fns.Count == 0) return null;
        if (choice is not ("auto" or "required") && !fns.Any(f => f["name"]!.GetValue<string>() == choice))
            throw new ArgumentException($"tool_choice names the function '{choice}', which is not in 'tools'.");
        return new ToolSetup(fmt, fns, choice, parallel, ToolFormats.SystemPrompt(fmt, fns, choice == "auto" ? null : choice));
    }

    /// <summary>OpenAI request: tools + tool_choice ("none" | "auto" | "required" | {"type":"function","function":{"name"}}).</summary>
    public static ToolSetup? ReadTools(JsonNode body, ToolFormat fmt)
    {
        if (body["tools"] is not JsonArray tools || tools.Count == 0) return null;
        string choice = "auto";
        if (body["tool_choice"] is JsonValue v && v.TryGetValue<string>(out var s)) choice = s;
        else if (body["tool_choice"] is JsonObject o) choice = o["function"]?["name"]?.GetValue<string>() ?? o["name"]?.GetValue<string>() ?? "auto";
        bool parallel = !(body["parallel_tool_calls"] is JsonValue p && p.TryGetValue<bool>(out var pb) && !pb);
        return Build(fmt, tools, choice, parallel);
    }

    /// <summary>Anthropic request: tools with input_schema + tool_choice {type: auto | any | tool | none}.</summary>
    public static ToolSetup? ReadAnthropicTools(JsonNode body, ToolFormat fmt)
    {
        if (body["tools"] is not JsonArray tools || tools.Count == 0) return null;
        var converted = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t?["name"]?.DeepClone(),
                ["description"] = t?["description"]?.DeepClone(),
                ["parameters"] = t?["input_schema"]?.DeepClone() ?? new JsonObject(),
            },
        }).ToArray());
        string choice = body["tool_choice"]?["type"]?.GetValue<string>() switch
        {
            "any" => "required",
            "none" => "none",
            "tool" => body["tool_choice"]?["name"]?.GetValue<string>() ?? "auto",
            _ => "auto",
        };
        bool parallel = body["tool_choice"]?["disable_parallel_tool_use"]?.GetValue<bool>() != true;
        return Build(fmt, converted, choice, parallel);
    }

    /// <summary>Puts the tool instructions into the system message and, for forced calls, constrains the output.</summary>
    public static void ApplyTools(ToolSetup setup, List<ChatMessage> messages, GenerationOptions g)
    {
        int si = messages.FindIndex(m => m.Role == "system");
        if (si >= 0) messages[si] = messages[si] with { Content = messages[si].Content + "\n\n" + setup.SystemPrompt };
        else messages.Insert(0, new ChatMessage("system", setup.SystemPrompt));
        if (setup.MustCall && string.IsNullOrWhiteSpace(g.Grammar) && string.IsNullOrWhiteSpace(g.JsonSchema) && !g.JsonMode)
            g.Grammar = ToolFormats.Grammar(setup.Format, setup.Functions, setup.ForcedName, setup.Parallel);
    }

    /// <summary>
    /// Converts the "messages" of an OpenAI request. Tool results and earlier tool calls are rendered the way the
    /// model's own template expects (see <see cref="ToolFormats"/>).
    /// </summary>
    public static List<ChatMessage> ReadMessages(JsonNode body, ToolFormat fmt = ToolFormat.Generic)
    {
        var list = new List<ChatMessage>();
        if (body["messages"] is not JsonArray arr) return list;
        var names = new Dictionary<string, string>();       // tool_call id -> function name
        foreach (var m in arr)
        {
            if (m is not JsonObject o) continue;
            string role = o["role"]?.GetValue<string>() ?? "user";
            var images = new List<byte[]>();
            string content = ContentText(o["content"], images);
            if (role == "tool" || role == "function")
            {
                string id = o["tool_call_id"]?.GetValue<string>() ?? "";
                string name = o["name"]?.GetValue<string>() ?? (names.TryGetValue(id, out var n) ? n : id);
                var (r, c) = ToolFormats.Result(fmt, name.Length > 0 ? name : "tool", content);
                list.Add(new ChatMessage(r, c));
            }
            else if (role == "assistant" && o["tool_calls"] is JsonArray calls && calls.Count > 0)
            {
                var pairs = new List<(string, JsonNode?)>();
                foreach (var c in calls)
                {
                    string nm = c?["function"]?["name"]?.GetValue<string>() ?? "";
                    if (c?["id"]?.GetValue<string>() is { } cid) names[cid] = nm;
                    pairs.Add((nm, ParseArgs(c?["function"]?["arguments"])));
                }
                list.Add(new ChatMessage("assistant", ToolFormats.EchoCalls(fmt, content, pairs)));
            }
            else
            {
                if (role == "developer") role = "system";
                list.Add(new ChatMessage(role, content, images.Count > 0 ? images : null));
            }
        }
        if (list.Sum(m => m.Images?.Count ?? 0) > ImageInput.MaxImagesPerRequest)
            throw new ArgumentException($"At most {ImageInput.MaxImagesPerRequest} images are accepted per request.");
        return list;
    }
}
