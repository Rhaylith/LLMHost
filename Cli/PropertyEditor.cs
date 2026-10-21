using System.Reflection;

namespace LLMHostProject.Cli;

/// <summary>Reflection based get/set so every option class is editable from the CLI without per-field code.</summary>
public static class PropertyEditor
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["temp"] = "temperature", ["topk"] = "topk", ["topp"] = "topp", ["minp"] = "minp", ["ctx"] = "contextsize",
        ["context"] = "contextsize", ["ngl"] = "gpulayers", ["layers"] = "gpulayers", ["max"] = "maxtokens",
        ["maxtokens"] = "maxtokens", ["predict"] = "maxtokens", ["system"] = "systemprompt", ["repeat"] = "repeatpenalty",
        ["flash"] = "flashattentionmode", ["fa"] = "flashattentionmode", ["mmap"] = "usemmap", ["mlock"] = "usemlock",
        ["kvk"] = "kvcachetypek", ["kvv"] = "kvcachetypev", ["ttl"] = "ttlminutes", ["id"] = "identifier",
        ["json"] = "jsonmode", ["think"] = "keepreasoning", ["reasoning"] = "keepreasoning", ["batch"] = "batchsize",
    };

    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").Replace(".", "").ToLowerInvariant();

    public static PropertyInfo? Find(Type t, string name)
    {
        string n = Norm(name);
        if (Aliases.TryGetValue(n, out var a)) n = a;
        return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => Norm(p.Name) == n && p.CanWrite);
    }

    public static IEnumerable<(string Name, string Value)> Dump(object o) =>
        o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => (p.Name, Format(p.GetValue(o))));

    public static string Format(object? v) => v switch
    {
        null => "(auto)",
        string s => s.Length == 0 ? "(empty)" : s,
        System.Collections.IEnumerable e when v is not string => string.Join(", ", e.Cast<object>()) is { Length: > 0 } j ? j : "(none)",
        bool b => b ? "on" : "off",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    public static bool TrySet(object target, string name, string raw, out string error)
    {
        error = "";
        var p = Find(target.GetType(), name);
        if (p == null) { error = $"unknown option '{name}'"; return false; }
        try
        {
            var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            bool nullable = Nullable.GetUnderlyingType(p.PropertyType) != null || !p.PropertyType.IsValueType;
            if (nullable && type != typeof(string) && raw is "auto" or "null" or "none" or "")
            {
                p.SetValue(target, null);
                return true;
            }
            object? value;
            if (type == typeof(bool))
                value = raw.ToLowerInvariant() is "on" or "true" or "1" or "yes" or "y";
            else if (type == typeof(string))
                value = raw is "(empty)" or "none" or "null" ? null : raw;
            else if (type == typeof(List<string>))
                value = raw is "none" or "" ? new List<string>() : raw.Split('|', ',').Select(x => x.Replace("\\n", "\n")).ToList();
            else if (type == typeof(Dictionary<int, float>))
            {
                var d = new Dictionary<int, float>();
                foreach (var part in raw.Split(','))
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2) d[int.Parse(kv[0])] = float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
                }
                value = d;
            }
            else
                value = Convert.ChangeType(raw, type, System.Globalization.CultureInfo.InvariantCulture);
            p.SetValue(target, value);
            return true;
        }
        catch (Exception ex)
        {
            error = $"invalid value '{raw}' for {p.Name}: {ex.Message}";
            return false;
        }
    }
}
