using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMHostProject.Inference;

/// <summary>The textual convention a model uses for tool calls.</summary>
public enum ToolFormat
{
    /// <summary>Unknown template: a JSON object {"tool_calls":[{"name":..,"arguments":..}]} requested through the prompt.</summary>
    Generic,
    /// <summary>&lt;tool_call&gt;{"name":..,"arguments":..}&lt;/tool_call&gt; (Qwen 2.5/3, Hermes, many fine-tunes).</summary>
    Hermes,
    /// <summary>[TOOL_CALLS][{"name":..,"arguments":..}] (Mistral family).</summary>
    Mistral,
    /// <summary>{"name":..,"parameters":..} optionally after &lt;|python_tag|&gt; (Llama 3.1+).</summary>
    Llama3,
}

public record ParsedToolCall(string Id, string Name, string Arguments);

public abstract record ToolEvent;
public sealed record ContentEvent(string Text) : ToolEvent;
public sealed record CallStartEvent(int Index, string Id, string Name) : ToolEvent;
public sealed record ArgsDeltaEvent(int Index, string Text) : ToolEvent;
public sealed record CallEndEvent(int Index) : ToolEvent;

/// <summary>Template-aware tool calling: prompts, history rendering, forced calls (grammar) and parsing.</summary>
public static class ToolFormats
{
    public static ToolFormat Detect(string? template)
    {
        if (string.IsNullOrEmpty(template)) return ToolFormat.Generic;
        if (template.Contains("<tool_call>")) return ToolFormat.Hermes;
        if (template.Contains("[TOOL_CALLS]") || template.Contains("[AVAILABLE_TOOLS]")) return ToolFormat.Mistral;
        if (template.Contains("<|python_tag|>") || template.Contains("<|start_header_id|>ipython") || template.Contains("Environment: ipython"))
            return ToolFormat.Llama3;
        return ToolFormat.Generic;
    }

    public static string NewId() => "call_" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>OpenAI-style function definitions (name, description, parameters) from a "tools" array.</summary>
    public static List<JsonObject> Functions(JsonArray tools)
    {
        var list = new List<JsonObject>();
        foreach (var t in tools)
        {
            var fn = (t?["function"] ?? t) as JsonObject;
            if (fn?["name"] == null) continue;
            list.Add((JsonObject)fn.DeepClone());
        }
        return list;
    }

    // ------------------------------------------------------------------ prompt

    public static string SystemPrompt(ToolFormat f, IReadOnlyList<JsonObject> fns, string? choice)
    {
        var sb = new StringBuilder();
        string required = choice switch
        {
            "required" => "\nYou MUST call at least one function; do not answer in plain text.",
            { } name when name != "auto" => $"\nYou MUST call the function \"{name}\"; do not answer in plain text.",
            _ => "",
        };
        switch (f)
        {
            case ToolFormat.Hermes:
                sb.AppendLine("# Tools\n\nYou may call one or more functions to assist with the user query.\n");
                sb.AppendLine("You are provided with function signatures within <tools></tools> XML tags:\n<tools>");
                foreach (var fn in fns) sb.AppendLine(new JsonObject { ["type"] = "function", ["function"] = fn.DeepClone() }.ToJsonString());
                sb.AppendLine("</tools>\n");
                sb.AppendLine("For each function call, return a json object with function name and arguments within <tool_call></tool_call> XML tags:");
                sb.AppendLine("<tool_call>\n{\"name\": <function-name>, \"arguments\": <args-json-object>}\n</tool_call>");
                break;
            case ToolFormat.Mistral:
                sb.AppendLine("You have access to the following tools:");
                sb.AppendLine("[AVAILABLE_TOOLS]" + new JsonArray(fns.Select(fn => (JsonNode)new JsonObject { ["type"] = "function", ["function"] = fn.DeepClone() }).ToArray()).ToJsonString() + "[/AVAILABLE_TOOLS]");
                sb.AppendLine("To call tools, answer with [TOOL_CALLS] followed by a JSON array: [TOOL_CALLS][{\"name\": \"<function-name>\", \"arguments\": {<args>}}]");
                break;
            case ToolFormat.Llama3:
                sb.AppendLine("Environment: ipython\n");
                sb.AppendLine("You have access to the following functions:");
                foreach (var fn in fns) sb.AppendLine(fn.ToJsonString());
                sb.AppendLine("\nIf you choose to call a function reply ONLY in the following format, one JSON object per call, and nothing else:");
                sb.AppendLine("{\"name\": function name, \"parameters\": dictionary of argument name and its value}");
                sb.AppendLine("Do not use variables. If no function is needed, answer normally.");
                break;
            default:
                sb.AppendLine("You can call tools. Available tools (JSON schema):");
                foreach (var fn in fns) sb.AppendLine(fn.ToJsonString());
                sb.AppendLine();
                sb.AppendLine("When a tool is needed, respond with ONLY a JSON object of the form " +
                              "{\"tool_calls\":[{\"name\":\"<tool name>\",\"arguments\":{...}}]} and nothing else. " +
                              "If no tool is needed, answer normally in plain text.");
                break;
        }
        sb.Append(required);
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ history

    /// <summary>An assistant turn that made calls, rendered the way the model itself would have written it.</summary>
    public static string EchoCalls(ToolFormat f, string? text, IEnumerable<(string Name, JsonNode? Args)> calls)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append('\n');
        var list = calls.ToList();
        switch (f)
        {
            case ToolFormat.Hermes:
                foreach (var (name, args) in list)
                    sb.Append("<tool_call>\n").Append(new JsonObject { ["name"] = name, ["arguments"] = args?.DeepClone() ?? new JsonObject() }.ToJsonString()).Append("\n</tool_call>\n");
                break;
            case ToolFormat.Mistral:
                sb.Append("[TOOL_CALLS]").Append(new JsonArray(list.Select(c => (JsonNode)new JsonObject
                    { ["name"] = c.Name, ["arguments"] = c.Args?.DeepClone() ?? new JsonObject() }).ToArray()).ToJsonString());
                break;
            case ToolFormat.Llama3:
                foreach (var (name, args) in list)
                    sb.Append(new JsonObject { ["name"] = name, ["parameters"] = args?.DeepClone() ?? new JsonObject() }.ToJsonString()).Append('\n');
                break;
            default:
                sb.Append(new JsonObject
                {
                    ["tool_calls"] = new JsonArray(list.Select(c => (JsonNode)new JsonObject
                        { ["name"] = c.Name, ["arguments"] = c.Args?.DeepClone() ?? new JsonObject() }).ToArray()),
                }.ToJsonString());
                break;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Result of a tool call as a chat message (role, content) the model understands.</summary>
    public static (string Role, string Content) Result(ToolFormat f, string name, string content) => f switch
    {
        ToolFormat.Hermes => ("user", $"<tool_response>\n{content}\n</tool_response>"),
        ToolFormat.Mistral => ("user", $"[TOOL_RESULTS]{content}[/TOOL_RESULTS]"),
        ToolFormat.Llama3 => ("user", $"[Result of {name}]\n{content}"),
        _ => ("user", $"[Result of tool call {name}]\n{content}"),
    };

    // ------------------------------------------------------------------ forced calls

    /// <summary>
    /// GBNF grammar that makes the model emit a valid call in its own format (tool_choice "required" or a named function).
    /// </summary>
    public static string? Grammar(ToolFormat f, IReadOnlyList<JsonObject> fns, string? forcedName, bool allowMany)
    {
        var chosen = forcedName != null ? fns.Where(x => x["name"]?.GetValue<string>() == forcedName).ToList() : fns.ToList();
        if (chosen.Count == 0) return null;
        var alts = new JsonArray();
        foreach (var fn in chosen)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["const"] = fn["name"]!.DeepClone() },
                    ["arguments"] = fn["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object" },
                },
                ["required"] = new JsonArray("name", "arguments"),
            };
            alts.Add(schema);
        }
        var root = chosen.Count == 1 ? alts[0]!.DeepClone() : new JsonObject { ["anyOf"] = alts };
        string g = SchemaGrammar.FromSchema(root.ToJsonString());
        // FromSchema's "root" becomes the call object; wrap it with the model's own framing
        g = Regex.Replace(g, @"^root ::=", "callobj ::=", RegexOptions.Multiline);
        string many(string item, string sep) => allowMany ? $"{item} ({sep} {item})*" : item;
        string top = f switch
        {
            ToolFormat.Hermes => "ws \"<tool_call>\" ws callobj ws \"</tool_call>\"" +
                                 (allowMany ? " (ws \"<tool_call>\" ws callobj ws \"</tool_call>\")*" : ""),
            ToolFormat.Mistral => "\"[TOOL_CALLS]\" \"[\" ws " + many("callobj", "\",\" ws") + " \"]\"",
            ToolFormat.Llama3 => "ws " + many("callobj", "ws"),
            _ => "\"{\" ws \"\\\"tool_calls\\\"\" ws \":\" ws \"[\" ws " + many("callobj", "\",\" ws") + " \"]\" ws \"}\"",
        };
        return "root ::= " + top + "\n" + g;
    }
}

/// <summary>
/// Incremental parser for the model output of a request that offered tools. Emits plain content, then for every call
/// its start (name), the argument text as it is generated, and its end.
/// </summary>
public class ToolStreamParser
{
    private readonly ToolFormat _format;
    private readonly StringBuilder _all = new();
    private int _contentEmitted;          // chars of _all already released as content
    private int _regionStart = -1;        // where the call region begins in _all
    private readonly List<CallState> _calls = new();

    private class CallState
    {
        public string Id = ToolFormats.NewId();
        public string? Name;
        public bool Started, Ended;
        public int ArgsEmitted;
        public string Arguments = "";
    }

    public ToolStreamParser(ToolFormat format) { _format = format; }

    /// <summary>Calls recognised so far (complete ones have an argument string).</summary>
    public IReadOnlyList<ParsedToolCall> Calls =>
        _calls.Where(c => c.Name != null).Select(c => new ParsedToolCall(c.Id, c.Name!, c.Arguments.Length == 0 ? "{}" : c.Arguments)).ToList();

    public bool HasCalls => _calls.Any(c => c.Name != null);

    private string Marker => _format switch
    {
        ToolFormat.Hermes => "<tool_call>",
        ToolFormat.Mistral => "[TOOL_CALLS]",
        _ => "",
    };

    public List<ToolEvent> Feed(string text)
    {
        _all.Append(text);
        return Step(final: false);
    }

    public List<ToolEvent> Finish()
    {
        return Step(final: true);
    }

    private List<ToolEvent> Step(bool final)
    {
        var events = new List<ToolEvent>();
        string all = _all.ToString();

        if (_regionStart < 0)
        {
            int start = FindRegion(all, final);
            if (start < 0)
            {
                // everything up to a possible partial marker is content
                int safe = final ? all.Length : all.Length - PartialMarker(all);
                if (_format is ToolFormat.Llama3 or ToolFormat.Generic && all.TrimStart().Length == 0) safe = _contentEmitted; // wait for the first real char
                if (safe > _contentEmitted)
                {
                    events.Add(new ContentEvent(all[_contentEmitted..safe]));
                    _contentEmitted = safe;
                }
                return events;
            }
            int contentEnd = Marker.Length > 0 ? start - Marker.Length : start;   // the marker itself is not content
            if (contentEnd > _contentEmitted)
            {
                string pre = all[_contentEmitted..contentEnd];
                if (pre.Trim().Length > 0) events.Add(new ContentEvent(pre.TrimEnd()));
            }
            _contentEmitted = Math.Max(_contentEmitted, start);
            _regionStart = start;
        }

        ParseRegion(all[_regionStart..], events, final);
        if (final && !HasCalls)
        {
            // it looked like a call but wasn't one (e.g. a plain JSON answer): hand the text back as content
            int begin = _format is ToolFormat.Hermes or ToolFormat.Mistral ? Math.Max(0, _regionStart - Marker.Length) : _regionStart;
            string raw = all[Math.Min(begin, all.Length)..];
            if (raw.Length > 0) events.Add(new ContentEvent(raw));
            _contentEmitted = all.Length;
        }
        return events;
    }

    /// <summary>Position where a tool call region begins, or -1 while the output still looks like plain text.</summary>
    private int FindRegion(string all, bool final)
    {
        string marker = Marker;
        if (marker.Length > 0)
        {
            int i = all.IndexOf(marker, StringComparison.Ordinal);
            return i < 0 ? -1 : i + marker.Length;
        }
        // Llama3 / generic: the answer is a bare JSON object (optionally after <|python_tag|>)
        string t = all.TrimStart();
        int lead = all.Length - t.Length;
        if (t.StartsWith("<|python_tag|>", StringComparison.Ordinal)) { lead += "<|python_tag|>".Length; t = all[lead..].TrimStart(); lead = all.Length - t.Length; }
        if (t.StartsWith("{") || t.StartsWith("[")) return lead;
        return -1;
    }

    private int PartialMarker(string all)
    {
        string marker = Marker;
        if (marker.Length == 0) return 0;
        for (int len = Math.Min(marker.Length - 1, all.Length); len > 0; len--)
            if (string.CompareOrdinal(all, all.Length - len, marker, 0, len) == 0) return len;
        return 0;
    }

    private void ParseRegion(string region, List<ToolEvent> events, bool final)
    {
        // generic wrapper {"tool_calls":[ ... ]}: skip to the array
        if (_format == ToolFormat.Generic)
        {
            var m = Regex.Match(region, "^\\s*\\{\\s*\"tool_calls\"\\s*:\\s*\\[");
            if (m.Success) region = region[m.Length..];
        }

        // top level objects of the region (inside an array or after the marker)
        var objects = new List<(int Start, int End, bool Complete)>();
        int depth = 0, objStart = -1; bool inStr = false, esc = false;
        for (int i = 0; i < region.Length; i++)
        {
            char c = region[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') { inStr = true; continue; }
            if (c == '{') { if (depth == 0) objStart = i; depth++; }
            else if (c == '}') { depth--; if (depth == 0 && objStart >= 0) { objects.Add((objStart, i + 1, true)); objStart = -1; } }
            else if (_format == ToolFormat.Hermes && depth == 0 && c == '<' && region.AsSpan(i).StartsWith("</tool_call>")) { /* separator */ }
        }
        if (depth > 0 && objStart >= 0) objects.Add((objStart, region.Length, false));

        for (int idx = 0; idx < objects.Count; idx++)
        {
            var (s, e, complete) = objects[idx];
            while (_calls.Count <= idx) _calls.Add(new CallState());
            var call = _calls[idx];
            if (call.Ended) continue;
            ScanObject(region[s..e], out string? name, out string? args, out bool argsDone);

            if (name != null && !call.Started)
            {
                call.Name = name; call.Started = true;
                events.Add(new CallStartEvent(idx, call.Id, name));
            }
            if (call.Started && args != null)
            {
                // only release complete JSON at the end for tidy output; stream the raw text as it grows
                string upto = args;
                call.Arguments = upto;
                if (upto.Length > call.ArgsEmitted)
                {
                    events.Add(new ArgsDeltaEvent(idx, upto[call.ArgsEmitted..]));
                    call.ArgsEmitted = upto.Length;
                }
            }
            if (complete && call.Started)
            {
                if (call.Arguments.Length == 0) { call.Arguments = "{}"; events.Add(new ArgsDeltaEvent(idx, "{}")); call.ArgsEmitted = 2; }
                call.Ended = true;
                events.Add(new CallEndEvent(idx));
            }
            else if (argsDone && call.Started && complete) { /* handled above */ }
        }
        if (final)
        {
            // an unterminated last call (cut off by max_tokens) is closed with whatever arguments it has
            foreach (var (c, i) in _calls.Select((c, i) => (c, i)))
                if (c.Started && !c.Ended)
                {
                    c.Ended = true;
                    if (c.Arguments.Length == 0) { c.Arguments = "{}"; events.Add(new ArgsDeltaEvent(i, "{}")); }
                    events.Add(new CallEndEvent(i));
                }
        }
    }

    /// <summary>
    /// Extracts the function name and the (possibly still incomplete) raw argument JSON from one call object.
    /// Understands {"name","arguments"|"parameters"} and the OpenAI shape {"function":{"name","arguments"}}.
    /// </summary>
    private static void ScanObject(string obj, out string? name, out string? args, out bool argsDone)
    {
        name = null; args = null; argsDone = false;
        int depth = 0; bool inStr = false, esc = false;
        for (int i = 0; i < obj.Length; i++)
        {
            char c = obj[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '{' || c == '[') { depth++; continue; }
            if (c == '}' || c == ']') { depth--; continue; }
            if (c != '"') continue;
            // a string: is it a key at depth 1 (call object) or 2 (inside "function")?
            int end = StringEnd(obj, i);
            if (end < 0) return;
            string key = obj.Substring(i + 1, end - i - 1);
            int after = end + 1;
            while (after < obj.Length && char.IsWhiteSpace(obj[after])) after++;
            bool isKey = after < obj.Length && obj[after] == ':' && depth is 1 or 2;
            if (!isKey) { i = end; continue; }
            int v = after + 1;
            while (v < obj.Length && char.IsWhiteSpace(obj[v])) v++;
            if (v >= obj.Length) return;
            if (key == "name" && obj[v] == '"')
            {
                int ne = StringEnd(obj, v);
                if (ne < 0) return;
                try { name = JsonSerializer.Deserialize<string>(obj.Substring(v, ne - v + 1)); } catch { name = obj.Substring(v + 1, ne - v - 1); }
                i = ne; continue;
            }
            if (key is "arguments" or "parameters")
            {
                if (obj[v] == '"')
                {
                    // arguments given as a JSON string: unwrap once complete
                    int se = StringEnd(obj, v);
                    if (se < 0) { args = null; return; }
                    try { args = JsonSerializer.Deserialize<string>(obj.Substring(v, se - v + 1)); } catch { args = obj.Substring(v + 1, se - v - 1); }
                    argsDone = true;
                    i = se; continue;
                }
                // raw object / array: copy up to its balanced end
                int d = 0; bool s2 = false, e2 = false; int j = v;
                for (; j < obj.Length; j++)
                {
                    char cj = obj[j];
                    if (s2) { if (e2) e2 = false; else if (cj == '\\') e2 = true; else if (cj == '"') s2 = false; continue; }
                    if (cj == '"') s2 = true;
                    else if (cj is '{' or '[') d++;
                    else if (cj is '}' or ']') { d--; if (d == 0) { j++; break; } }
                    else if (d == 0 && (cj == ',' || cj == '}')) break;
                }
                args = obj.Substring(v, j - v);
                argsDone = d == 0 && j <= obj.Length && (args.Length > 0) && (args[^1] is '}' or ']');
                i = j - 1; continue;
            }
        }
    }

    private static int StringEnd(string s, int openQuote)
    {
        bool esc = false;
        for (int i = openQuote + 1; i < s.Length; i++)
        {
            if (esc) { esc = false; continue; }
            if (s[i] == '\\') esc = true;
            else if (s[i] == '"') return i;
        }
        return -1;
    }

    /// <summary>One-shot parse of a finished answer: content text and calls.</summary>
    public static (string Content, List<ParsedToolCall> Calls) ParseAll(ToolFormat format, string text)
    {
        var p = new ToolStreamParser(format);
        var content = new StringBuilder();
        foreach (var e in p.Feed(text)) if (e is ContentEvent c) content.Append(c.Text);
        foreach (var e in p.Finish()) if (e is ContentEvent c) content.Append(c.Text);
        return (content.ToString(), p.Calls.ToList());
    }
}
