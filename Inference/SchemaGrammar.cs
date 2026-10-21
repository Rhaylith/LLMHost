using System.Text;
using System.Text.Json;

namespace LLMHostProject.Inference;

/// <summary>Converts a JSON Schema into a llama.cpp GBNF grammar so output is guaranteed to match it.</summary>
public class SchemaGrammar
{
    public const string JsonObjectGrammar = """
        root ::= object
        value ::= object | array | string | number | ("true" | "false" | "null") ws
        object ::= "{" ws ( string ":" ws value ("," ws string ":" ws value)* )? "}" ws
        array ::= "[" ws ( value ("," ws value)* )? "]" ws
        string ::= "\"" ( [^"\\\x7F\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F]{4}) )* "\"" ws
        number ::= ("-"? ([0-9] | [1-9] [0-9]{0,15})) ("." [0-9]+)? ([eE] [-+]? [0-9] [1-9]{0,15})? ws
        ws ::= | " " | "\n" [ \t]{0,20}
        """;

    private readonly Dictionary<string, string> _rules = new();
    private readonly JsonElement _root;
    private int _counter;

    private SchemaGrammar(JsonElement root) { _root = root; }

    public static string FromSchema(string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        var g = new SchemaGrammar(doc.RootElement.Clone());
        g._rules["ws"] = "| \" \" | \"\\n\" [ \\t]{0,20}";
        g._rules["string"] = "\"\\\"\" ( [^\"\\\\\\x7F\\x00-\\x1F] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" [0-9a-fA-F]{4}) )* \"\\\"\" ws";
        g._rules["number"] = "(\"-\"? ([0-9] | [1-9] [0-9]{0,15})) (\".\" [0-9]+)? ([eE] [-+]? [0-9] [1-9]{0,15})? ws";
        g._rules["integer"] = "\"-\"? ([0-9] | [1-9] [0-9]{0,15}) ws";
        g._rules["boolean"] = "(\"true\" | \"false\") ws";
        g._rules["null"] = "\"null\" ws";
        g._rules["anyvalue"] = "anyobject | anyarray | string | number | boolean | null";
        g._rules["anyobject"] = "\"{\" ws ( string \":\" ws anyvalue (\",\" ws string \":\" ws anyvalue)* )? \"}\" ws";
        g._rules["anyarray"] = "\"[\" ws ( anyvalue (\",\" ws anyvalue)* )? \"]\" ws";
        string rootRule = g.Visit(g._root, "root");
        g._rules["root"] = rootRule;
        var sb = new StringBuilder();
        sb.AppendLine("root ::= " + rootRule);
        foreach (var (name, body) in g._rules)
            if (name != "root") sb.AppendLine($"{name} ::= {body}");
        return sb.ToString();
    }

    private string Visit(JsonElement s, string hint)
    {
        if (s.ValueKind != JsonValueKind.Object) return "anyvalue";

        if (s.TryGetProperty("$ref", out var rf) && rf.GetString() is { } refPath && refPath.StartsWith("#/"))
        {
            JsonElement cur = _root;
            foreach (var seg in refPath[2..].Split('/'))
                if (!cur.TryGetProperty(seg, out cur)) return "anyvalue";
            return Visit(cur, hint);
        }
        if (s.TryGetProperty("const", out var c)) return Literal(c);
        if (s.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
            return "(" + string.Join(" | ", en.EnumerateArray().Select(Literal)) + ") ws";
        foreach (var key in new[] { "anyOf", "oneOf" })
            if (s.TryGetProperty(key, out var alts) && alts.ValueKind == JsonValueKind.Array)
            {
                var names = alts.EnumerateArray().Select((a, i) => Define(Visit(a, $"{hint}-{i}"), $"{hint}-alt{i}")).ToList();
                return string.Join(" | ", names);
            }

        string? type = null;
        if (s.TryGetProperty("type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String) type = t.GetString();
            else if (t.ValueKind == JsonValueKind.Array)
            {
                var names = t.EnumerateArray().Select(x => x.GetString()!).Select(tt =>
                {
                    var copy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.GetRawText())!;
                    copy["type"] = JsonDocument.Parse($"\"{tt}\"").RootElement.Clone();
                    var el = JsonDocument.Parse(JsonSerializer.Serialize(copy)).RootElement.Clone();
                    return Define(Visit(el, hint + "-" + tt), hint + "-" + tt);
                });
                return string.Join(" | ", names);
            }
        }
        if (type == null && s.TryGetProperty("properties", out _)) type = "object";

        switch (type)
        {
            case "string": return "string";
            case "number": return "number";
            case "integer": return "integer";
            case "boolean": return "boolean";
            case "null": return "null";
            case "array":
                {
                    string item = s.TryGetProperty("items", out var items) ? Define(Visit(items, hint + "-item"), hint + "-item") : "anyvalue";
                    int min = s.TryGetProperty("minItems", out var mi) ? mi.GetInt32() : 0;
                    return min > 0
                        ? $"\"[\" ws {item} (\",\" ws {item})* \"]\" ws"
                        : $"\"[\" ws ({item} (\",\" ws {item})*)? \"]\" ws";
                }
            case "object":
                {
                    if (!s.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
                        return "anyobject";
                    var required = new HashSet<string>();
                    if (s.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                        foreach (var r in req.EnumerateArray()) required.Add(r.GetString()!);
                    var parts = new List<(string Text, bool Required)>();
                    foreach (var p in props.EnumerateObject())
                    {
                        string valueRule = Define(Visit(p.Value, hint + "-" + p.Name), hint + "-" + p.Name);
                        string kv = $"{Quote(JsonSerializer.Serialize(p.Name))} ws \":\" ws {valueRule}";
                        parts.Add((kv, required.Contains(p.Name)));
                    }
                    // if nothing is required, treat every property as required to keep the grammar unambiguous
                    if (!parts.Any(x => x.Required)) parts = parts.Select(x => (x.Text, true)).ToList();
                    var sb = new StringBuilder("\"{\" ws ");
                    bool first = true;
                    foreach (var (text, isReq) in parts)
                    {
                        if (isReq) { sb.Append(first ? text : $" \",\" ws {text}"); first = false; }
                        else sb.Append($" (\",\" ws {text})?");
                    }
                    sb.Append(" \"}\" ws");
                    return sb.ToString();
                }
            default:
                return "anyvalue";
        }
    }

    private string Define(string body, string hint)
    {
        // reuse built-in single-token rules
        if (!body.Contains(' ') && !body.Contains('|') && _rules.ContainsKey(body)) return body;
        string name = Sanitize(hint) + "-" + (++_counter);
        _rules[name] = body;
        return name;
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        return sb.Length == 0 ? "r" : sb.ToString().Trim('-');
    }

    private static string Literal(JsonElement e) => Quote(e.GetRawText()) + " ws";

    private static string Quote(string raw) => "\"" + raw.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
