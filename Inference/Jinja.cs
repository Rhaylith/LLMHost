using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LLMHostProject.Inference;

/// <summary>A template could not be parsed or rendered (unsupported syntax, <c>raise_exception</c>, wrong types).</summary>
public sealed class JinjaException : Exception
{
    public JinjaException(string message) : base(message) { }
}

/// <summary>Value of a name or attribute that does not exist: false, prints as nothing, <c>is defined</c> is false.</summary>
public sealed class JinjaUndefined
{
    public static readonly JinjaUndefined Instance = new();
    private JinjaUndefined() { }
    public override string ToString() => "";
}

/// <summary>Result of <c>namespace(...)</c>: a bag of attributes that can be assigned inside loops.</summary>
public sealed class JinjaNamespace : Dictionary<string, object?> { }

public delegate object? JinjaCallable(IReadOnlyList<object?> args, IReadOnlyDictionary<string, object?> kwargs);

/// <summary>
/// A small interpreter for the Jinja2 subset that chat templates use (the <c>chat_template</c> of GGUF / Hugging Face models):
/// <c>if/elif/else, for (loop.*, else, break, continue), set (also <c>ns.x</c> and blocks), macro, raw</c>, whitespace control with
/// <c>trim_blocks</c>/<c>lstrip_blocks</c> as Hugging Face configures them, expressions with filters, tests, slices, method calls
/// on strings / lists / dicts, and the helpers <c>raise_exception</c>, <c>strftime_now</c>, <c>namespace</c>, <c>range</c>, <c>dict</c>.
/// Values are plain .NET objects (string, long, double, bool, null, List&lt;object?&gt;, Dictionary&lt;string, object?&gt;).
/// There is no access to anything outside the data given to <see cref="Render"/>.
/// </summary>
public sealed class JinjaTemplate
{
    private readonly List<Node> _body;
    public string Source { get; }

    public JinjaTemplate(string source)
    {
        Source = source;
        _body = new Parser(Lexer.Lex(source)).ParseAll();
    }

    /// <summary>Renders the template with the given variables.</summary>
    public string Render(IDictionary<string, object?> variables)
    {
        var scope = new Scope(null);
        foreach (var (k, v) in Builtins.Globals) scope.Set(k, v);
        foreach (var (k, v) in variables) scope.Set(k, v);
        var sb = new StringBuilder();
        new Interpreter(sb).ExecBlock(_body, scope);
        return sb.ToString();
    }

    /// <summary>Converts parsed JSON into the plain objects templates work with.</summary>
    public static object? FromJson(JsonNode? n) => n switch
    {
        null => null,
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => FromJson(kv.Value), StringComparer.Ordinal) is var d ? new Dictionary<string, object?>(d) : null,
        JsonArray a => a.Select(FromJson).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var dbl) => dbl,
        _ => n.ToString(),
    };

    // ====================================================================== lexer

    private enum Kind { Text, Var, Block, Comment }
    private sealed class Chunk
    {
        public Kind Kind; public string Text = ""; public bool TrimBefore, TrimAfter, KeepIndent;
    }

    private static class Lexer
    {
        public static List<Chunk> Lex(string src)
        {
            var chunks = new List<Chunk>();
            int pos = 0;
            while (pos < src.Length)
            {
                int next = IndexOfOpen(src, pos);
                if (next < 0) { chunks.Add(new Chunk { Kind = Kind.Text, Text = src[pos..] }); break; }
                if (next > pos) chunks.Add(new Chunk { Kind = Kind.Text, Text = src[pos..next] });
                char type = src[next + 1];
                string close = type == '{' ? "}}" : type == '%' ? "%}" : "#}";
                int bodyStart = next + 2;
                var c = new Chunk { Kind = type == '{' ? Kind.Var : type == '%' ? Kind.Block : Kind.Comment };
                if (bodyStart < src.Length && src[bodyStart] == '-') { c.TrimBefore = true; bodyStart++; }
                else if (bodyStart < src.Length && src[bodyStart] == '+') { c.KeepIndent = true; bodyStart++; }
                int end = FindClose(src, bodyStart, close, c.Kind != Kind.Comment);
                if (end < 0) throw new JinjaException($"Unclosed '{src.Substring(next, 2)}' at offset {next}.");
                int bodyEnd = end;
                if (bodyEnd > bodyStart && src[bodyEnd - 1] == '-') { c.TrimAfter = true; bodyEnd--; }
                c.Text = src[bodyStart..bodyEnd].Trim();
                pos = end + 2;
                bool isRaw = c.Kind == Kind.Block && c.Text == "raw";
                if (!isRaw) chunks.Add(c);
                if (isRaw)
                {
                    var m = Regex.Match(src[pos..], @"\{%-?\s*endraw\s*-?%\}");
                    if (!m.Success) throw new JinjaException("'raw' without 'endraw'.");
                    chunks.Add(new Chunk { Kind = Kind.Text, Text = src.Substring(pos, m.Index) });
                    pos += m.Index + m.Length;
                }
            }
            Whitespace(chunks);
            return chunks;
        }

        private static int IndexOfOpen(string s, int from)
        {
            for (int i = from; i < s.Length - 1; i++)
                if (s[i] == '{' && (s[i + 1] == '{' || s[i + 1] == '%' || s[i + 1] == '#')) return i;
            return -1;
        }

        private static int FindClose(string s, int from, string close, bool honourStrings)
        {
            char quote = '\0';
            for (int i = from; i < s.Length - 1; i++)
            {
                char ch = s[i];
                if (quote != '\0')
                {
                    if (ch == '\\') i++;
                    else if (ch == quote) quote = '\0';
                    continue;
                }
                if (honourStrings && (ch == '"' || ch == '\'')) { quote = ch; continue; }
                if (s[i] == close[0] && s[i + 1] == close[1]) return i;
            }
            return -1;
        }

        /// <summary>Whitespace control: <c>-</c> markers, plus Hugging Face's trim_blocks and lstrip_blocks.</summary>
        private static void Whitespace(List<Chunk> chunks)
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                if (c.Kind == Kind.Text) continue;
                bool block = c.Kind != Kind.Var;
                if (i > 0 && chunks[i - 1].Kind == Kind.Text)
                {
                    var prev = chunks[i - 1];
                    if (c.TrimBefore) prev.Text = prev.Text.TrimEnd();
                    else if (block && !c.KeepIndent)
                    {
                        // lstrip_blocks: indentation in front of a block tag goes away when it is alone on its line
                        int nl = prev.Text.LastIndexOf('\n');
                        string tail = prev.Text[(nl + 1)..];
                        if (tail.All(ch => ch == ' ' || ch == '\t') && (nl >= 0 || i - 1 == 0)) prev.Text = prev.Text[..(nl + 1)];
                    }
                }
                if (i + 1 < chunks.Count && chunks[i + 1].Kind == Kind.Text)
                {
                    var nextChunk = chunks[i + 1];
                    if (c.TrimAfter) nextChunk.Text = nextChunk.Text.TrimStart();
                    else if (block)
                    {
                        // trim_blocks: the newline right after a block tag is dropped
                        if (nextChunk.Text.StartsWith("\r\n")) nextChunk.Text = nextChunk.Text[2..];
                        else if (nextChunk.Text.StartsWith('\n')) nextChunk.Text = nextChunk.Text[1..];
                    }
                }
            }
        }
    }

    // ====================================================================== AST

    private abstract class Node { }
    private sealed class TextNode(string text) : Node { public string Text = text; }
    private sealed class OutputNode(Expr e) : Node { public Expr E = e; }
    private sealed class IfNode : Node { public List<(Expr Cond, List<Node> Body)> Branches = new(); public List<Node>? Else; }
    private sealed class ForNode : Node
    {
        public List<string> Targets = new(); public Expr Iter = null!; public Expr? Filter; public List<Node> Body = new(); public List<Node>? Else;
    }
    private sealed class SetNode : Node { public Expr Target = null!; public Expr? Value; public List<Node>? Block; }
    private sealed class MacroNode : Node { public string Name = ""; public List<(string Name, Expr? Default)> Params = new(); public List<Node> Body = new(); }
    private sealed class FlowNode(bool isBreak) : Node { public bool IsBreak = isBreak; }
    private sealed class PassNode(List<Node> body) : Node { public List<Node> Body = body; }
    private sealed class DoNode(Expr e) : Node { public Expr E = e; }

    private abstract class Expr { }
    private sealed class Lit(object? v) : Expr { public object? V = v; }
    private sealed class NameE(string n) : Expr { public string Name = n; }
    private sealed class AttrE(Expr o, string n) : Expr { public Expr Obj = o; public string Name = n; }
    private sealed class IndexE(Expr o, Expr i) : Expr { public Expr Obj = o, Index = i; }
    private sealed class SliceE(Expr o, Expr? a, Expr? b, Expr? c) : Expr { public Expr Obj = o; public Expr? Start = a, Stop = b, Step = c; }
    private sealed class CallE(Expr f, List<Expr> args, Dictionary<string, Expr> kw) : Expr { public Expr Fn = f; public List<Expr> Args = args; public Dictionary<string, Expr> Kw = kw; }
    private sealed class FilterE(Expr v, string name, List<Expr> args, Dictionary<string, Expr> kw) : Expr { public Expr Value = v; public string Name = name; public List<Expr> Args = args; public Dictionary<string, Expr> Kw = kw; }
    private sealed class TestE(Expr v, string name, List<Expr> args, bool negate) : Expr { public Expr Value = v; public string Name = name; public List<Expr> Args = args; public bool Negate = negate; }
    private sealed class UnaryE(string op, Expr e) : Expr { public string Op = op; public Expr E = e; }
    private sealed class BinaryE(string op, Expr l, Expr r) : Expr { public string Op = op; public Expr L = l, R = r; }
    private sealed class CondE(Expr then, Expr cond, Expr? other) : Expr { public Expr Then = then, Cond = cond; public Expr? Else = other; }
    private sealed class ListE(List<Expr> items) : Expr { public List<Expr> Items = items; }
    private sealed class DictE(List<(Expr K, Expr V)> items) : Expr { public List<(Expr K, Expr V)> Items = items; }

    // ====================================================================== parser

    private sealed class Parser
    {
        private readonly List<Chunk> _chunks;
        private int _i;
        public Parser(List<Chunk> chunks) => _chunks = chunks;

        public List<Node> ParseAll()
        {
            var body = ParseBody(out var stop);
            if (stop != null) throw new JinjaException($"Unexpected '{stop}'.");
            return body;
        }

        /// <summary>Parses until a block tag from <paramref name="stops"/>; returns its first word through <paramref name="stop"/>.</summary>
        private List<Node> ParseBody(out string? stop, params string[] stops)
        {
            var nodes = new List<Node>();
            stop = null;
            while (_i < _chunks.Count)
            {
                var c = _chunks[_i++];
                switch (c.Kind)
                {
                    case Kind.Text: if (c.Text.Length > 0) nodes.Add(new TextNode(c.Text)); break;
                    case Kind.Comment: break;
                    case Kind.Var: nodes.Add(new OutputNode(new ExprParser(c.Text).ParseFull())); break;
                    case Kind.Block:
                        string word = c.Text.Split(' ', '\t', '\n', '\r')[0];
                        string rest = c.Text[word.Length..].Trim();
                        if (stops.Contains(word)) { stop = word; _stopArgs = rest; return nodes; }
                        nodes.Add(ParseBlock(word, rest));
                        break;
                }
            }
            if (stops.Length > 0) throw new JinjaException($"Missing '{stops[^1]}'.");
            return nodes;
        }

        private string _stopArgs = "";

        private Node ParseBlock(string word, string rest)
        {
            switch (word)
            {
                case "if":
                {
                    var n = new IfNode();
                    string cond = rest;
                    while (true)
                    {
                        var body = ParseBody(out var stop, "elif", "else", "endif");
                        n.Branches.Add((new ExprParser(cond).ParseFull(), body));
                        if (stop == "elif") { cond = _stopArgs; continue; }
                        if (stop == "else") { n.Else = ParseBody(out _, "endif"); }
                        return n;
                    }
                }
                case "for":
                {
                    var m = Regex.Match(rest, @"^(?<t>.+?)\s+in\s+(?<i>.+)$", RegexOptions.Singleline);
                    if (!m.Success) throw new JinjaException($"Bad for statement: {rest}");
                    var n = new ForNode();
                    n.Targets = m.Groups["t"].Value.Split(',').Select(s => s.Trim().Trim('(', ')').Trim()).Where(s => s.Length > 0).ToList();
                    string iter = m.Groups["i"].Value;
                    var fm = Regex.Match(iter, @"^(?<i>.+?)\s+if\s+(?<c>.+)$", RegexOptions.Singleline);
                    if (fm.Success && !iter.Contains(" else ")) { iter = fm.Groups["i"].Value; n.Filter = new ExprParser(fm.Groups["c"].Value).ParseFull(); }
                    n.Iter = new ExprParser(iter).ParseFull();
                    n.Body = ParseBody(out var stop, "else", "endfor");
                    if (stop == "else") n.Else = ParseBody(out _, "endfor");
                    return n;
                }
                case "set":
                {
                    var m = Regex.Match(rest, @"^(?<t>[^=]+?)\s*=(?!=)\s*(?<v>.+)$", RegexOptions.Singleline);
                    if (!m.Success)
                    {
                        // {% set x %}...{% endset %}
                        var block = ParseBody(out _, "endset");
                        return new SetNode { Target = new ExprParser(rest).ParseFull(), Block = block };
                    }
                    return new SetNode { Target = new ExprParser(m.Groups["t"].Value).ParseFull(), Value = new ExprParser(m.Groups["v"].Value).ParseFull() };
                }
                case "macro":
                {
                    var m = Regex.Match(rest, @"^(?<n>\w+)\s*\((?<a>.*)\)\s*$", RegexOptions.Singleline);
                    if (!m.Success) throw new JinjaException($"Bad macro: {rest}");
                    var n = new MacroNode { Name = m.Groups["n"].Value };
                    foreach (var part in new ExprParser(m.Groups["a"].Value).SplitTopLevel())
                    {
                        int eq = part.IndexOf('=');
                        if (eq > 0 && part[eq + 1] != '=') n.Params.Add((part[..eq].Trim(), new ExprParser(part[(eq + 1)..]).ParseFull()));
                        else if (part.Trim().Length > 0) n.Params.Add((part.Trim(), null));
                    }
                    n.Body = ParseBody(out _, "endmacro");
                    return n;
                }
                case "break": return new FlowNode(true);
                case "continue": return new FlowNode(false);
                case "generation": return new PassNode(ParseBody(out _, "endgeneration"));
                case "do": return new DoNode(new ExprParser(rest).ParseFull());
                default: throw new JinjaException($"Unsupported tag '{word}'.");
            }
        }
    }

    // ---------------------------------------------------------------------- expression parser

    private sealed class ExprParser
    {
        private readonly List<(string Kind, string Text)> _t = new();
        private int _p;
        private readonly string _src;

        public ExprParser(string src)
        {
            _src = src;
            Tokenize(src);
        }

        private static readonly string[] Ops = { "==", "!=", "<=", ">=", "//", "**", "+", "-", "*", "/", "%", "~", "<", ">", "=", "(", ")", "[", "]", "{", "}", ",", ".", ":", "|" };

        private void Tokenize(string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"' || c == '\'')
                {
                    var sb = new StringBuilder(); int j = i + 1;
                    while (j < s.Length && s[j] != c)
                    {
                        if (s[j] == '\\' && j + 1 < s.Length)
                        {
                            j++;
                            sb.Append(s[j] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '\'' => '\'', '"' => '"', '0' => '\0', var o => o });
                        }
                        else sb.Append(s[j]);
                        j++;
                    }
                    if (j >= s.Length) throw new JinjaException($"Unterminated string in: {s}");
                    _t.Add(("str", sb.ToString())); i = j + 1; continue;
                }
                if (char.IsDigit(c))
                {
                    int j = i; while (j < s.Length && (char.IsDigit(s[j]) || (s[j] == '.' && j + 1 < s.Length && char.IsDigit(s[j + 1])))) j++;
                    _t.Add(("num", s[i..j])); i = j; continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i; while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    _t.Add(("name", s[i..j])); i = j; continue;
                }
                string? op = Ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
                if (op == null) throw new JinjaException($"Unexpected character '{c}' in: {s}");
                _t.Add(("op", op)); i += op.Length;
            }
        }

        /// <summary>Splits "a, b=1, c" at top-level commas (macro parameter lists).</summary>
        public IEnumerable<string> SplitTopLevel()
        {
            int depth = 0, start = 0; char quote = '\0';
            for (int i = 0; i < _src.Length; i++)
            {
                char c = _src[i];
                if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') quote = c;
                else if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == ',' && depth == 0) { yield return _src[start..i]; start = i + 1; }
            }
            if (start < _src.Length) yield return _src[start..];
        }

        public Expr ParseFull()
        {
            var e = ParseTernary();
            // tuple without parentheses: "a, b"
            if (Peek("op", ","))
            {
                var items = new List<Expr> { e };
                while (Accept("op", ",")) { if (_p >= _t.Count) break; items.Add(ParseTernary()); }
                e = new ListE(items);
            }
            if (_p < _t.Count) throw new JinjaException($"Unexpected '{_t[_p].Text}' in: {_src}");
            return e;
        }

        private bool Peek(string kind, string text) => _p < _t.Count && _t[_p].Kind == kind && _t[_p].Text == text;
        private bool Accept(string kind, string text) { if (Peek(kind, text)) { _p++; return true; } return false; }
        private void Expect(string kind, string text) { if (!Accept(kind, text)) throw new JinjaException($"Expected '{text}' in: {_src}"); }

        private Expr ParseTernary()
        {
            var e = ParseOr();
            if (Accept("name", "if"))
            {
                var cond = ParseOr();
                Expr? other = null;
                if (Accept("name", "else")) other = ParseTernary();
                return new CondE(e, cond, other);
            }
            return e;
        }

        private Expr ParseOr() { var l = ParseAnd(); while (Accept("name", "or")) l = new BinaryE("or", l, ParseAnd()); return l; }
        private Expr ParseAnd() { var l = ParseNot(); while (Accept("name", "and")) l = new BinaryE("and", l, ParseNot()); return l; }
        private Expr ParseNot() => Accept("name", "not") ? new UnaryE("not", ParseNot()) : ParseCompare();

        private Expr ParseCompare()
        {
            var l = ParseConcat();
            while (true)
            {
                if (_p < _t.Count && _t[_p].Kind == "op" && (_t[_p].Text is "==" or "!=" or "<" or ">" or "<=" or ">="))
                { string op = _t[_p++].Text; l = new BinaryE(op, l, ParseConcat()); }
                else if (Accept("name", "in")) l = new BinaryE("in", l, ParseConcat());
                else if (Peek("name", "not") && _p + 1 < _t.Count && _t[_p + 1] is ("name", "in")) { _p += 2; l = new UnaryE("not", new BinaryE("in", l, ParseConcat())); }
                else if (Accept("name", "is"))
                {
                    bool neg = Accept("name", "not");
                    string name = _t[_p++].Text;
                    var args = new List<Expr>();
                    if (Accept("op", "(")) { args = ParseArgs(out _); }
                    else if (_p < _t.Count && (_t[_p].Kind is "str" or "num" || (_t[_p].Kind == "name" && _t[_p].Text is not ("and" or "or" or "if" or "else" or "in" or "is" or "not"))))
                        args.Add(ParseUnary());
                    l = new TestE(l, name, args, neg);
                }
                else return l;
            }
        }

        private Expr ParseConcat() { var l = ParseAdd(); while (Accept("op", "~")) l = new BinaryE("~", l, ParseAdd()); return l; }
        private Expr ParseAdd()
        {
            var l = ParseMul();
            while (_p < _t.Count && _t[_p].Kind == "op" && _t[_p].Text is "+" or "-") { string op = _t[_p++].Text; l = new BinaryE(op, l, ParseMul()); }
            return l;
        }
        private Expr ParseMul()
        {
            var l = ParseUnary();
            while (_p < _t.Count && _t[_p].Kind == "op" && _t[_p].Text is "*" or "/" or "//" or "%" or "**") { string op = _t[_p++].Text; l = new BinaryE(op, l, ParseUnary()); }
            return l;
        }
        private Expr ParseUnary()
        {
            if (Accept("op", "-")) return new UnaryE("-", ParseUnary());
            if (Accept("op", "+")) return ParseUnary();
            return ParseFilter(ParsePostfix());
        }

        private Expr ParseFilter(Expr e)
        {
            while (Accept("op", "|"))
            {
                string name = _t[_p++].Text;
                while (Accept("op", ".")) name += "." + _t[_p++].Text;
                var args = new List<Expr>(); var kw = new Dictionary<string, Expr>();
                if (Accept("op", "(")) args = ParseArgs(out kw);
                e = new FilterE(e, name, args, kw);
            }
            return e;
        }

        private List<Expr> ParseArgs(out Dictionary<string, Expr> kw)
        {
            var args = new List<Expr>(); kw = new Dictionary<string, Expr>();
            while (!Accept("op", ")"))
            {
                if (_p + 1 < _t.Count && _t[_p].Kind == "name" && _t[_p + 1] is ("op", "="))
                { string k = _t[_p].Text; _p += 2; kw[k] = ParseTernary(); }
                else args.Add(ParseTernary());
                if (!Accept("op", ",") && !Peek("op", ")")) throw new JinjaException($"Bad argument list in: {_src}");
            }
            return args;
        }

        private Expr ParsePostfix()
        {
            var e = ParsePrimary();
            while (true)
            {
                if (Accept("op", ".")) e = new AttrE(e, _t[_p++].Text);
                else if (Accept("op", "[")) e = ParseSubscript(e);
                else if (Accept("op", "(")) { var args = ParseArgs(out var kw); e = new CallE(e, args, kw); }
                else return e;
            }
        }

        private Expr ParseSubscript(Expr obj)
        {
            Expr? a = null, b = null, c = null; bool slice = false;
            if (!Peek("op", ":")) a = ParseTernary();
            if (Accept("op", ":"))
            {
                slice = true;
                if (!Peek("op", "]") && !Peek("op", ":")) b = ParseTernary();
                if (Accept("op", ":")) { if (!Peek("op", "]")) c = ParseTernary(); }
            }
            Expect("op", "]");
            return slice ? new SliceE(obj, a, b, c) : new IndexE(obj, a!);
        }

        private Expr ParsePrimary()
        {
            if (_p >= _t.Count) throw new JinjaException($"Unexpected end of expression: {_src}");
            var (kind, text) = _t[_p++];
            switch (kind)
            {
                case "str":
                {
                    var sb = new StringBuilder(text);                  // adjacent literals are concatenated, like Python
                    while (_p < _t.Count && _t[_p].Kind == "str") sb.Append(_t[_p++].Text);
                    return new Lit(sb.ToString());
                }
                case "num": return new Lit(text.Contains('.') ? double.Parse(text, CultureInfo.InvariantCulture) : long.Parse(text, CultureInfo.InvariantCulture));
                case "name":
                    switch (text)
                    {
                        case "true": case "True": return new Lit(true);
                        case "false": case "False": return new Lit(false);
                        case "none": case "None": return new Lit(null);
                        default: return new NameE(text);
                    }
                case "op":
                    if (text == "(")
                    {
                        if (Accept("op", ")")) return new ListE(new());
                        var e = ParseTernary();
                        if (Peek("op", ","))
                        {
                            var items = new List<Expr> { e };
                            while (Accept("op", ",")) { if (Peek("op", ")")) break; items.Add(ParseTernary()); }
                            Expect("op", ")");
                            return new ListE(items);
                        }
                        Expect("op", ")");
                        return e;
                    }
                    if (text == "[")
                    {
                        var items = new List<Expr>();
                        while (!Accept("op", "]")) { items.Add(ParseTernary()); if (!Accept("op", ",") && !Peek("op", "]")) throw new JinjaException($"Bad list in: {_src}"); }
                        return new ListE(items);
                    }
                    if (text == "{")
                    {
                        var items = new List<(Expr, Expr)>();
                        while (!Accept("op", "}"))
                        {
                            var k = ParseTernary(); Expect("op", ":"); var v = ParseTernary(); items.Add((k, v));
                            if (!Accept("op", ",") && !Peek("op", "}")) throw new JinjaException($"Bad dict in: {_src}");
                        }
                        return new DictE(items);
                    }
                    break;
            }
            throw new JinjaException($"Unexpected '{text}' in: {_src}");
        }
    }

    // ====================================================================== interpreter

    private sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, object?> _vars = new(StringComparer.Ordinal);
        public Scope? Parent = parent;
        public bool TryGet(string name, out object? v)
        {
            for (var s = this; s != null; s = s.Parent) if (s._vars.TryGetValue(name, out v)) return true;
            v = null; return false;
        }
        public void Set(string name, object? v) => _vars[name] = v;
    }

    private enum Flow { None, Break, Continue }

    private sealed class MacroValue(MacroNode node, Scope closure, Interpreter interp)
    {
        public object? Call(IReadOnlyList<object?> args, IReadOnlyDictionary<string, object?> kwargs)
        {
            var scope = new Scope(closure);
            for (int i = 0; i < node.Params.Count; i++)
            {
                var (name, def) = node.Params[i];
                object? v;
                if (i < args.Count) v = args[i];
                else if (kwargs.TryGetValue(name, out var kv)) v = kv;
                else v = def != null ? interp.Eval(def, closure) : JinjaUndefined.Instance;
                scope.Set(name, v);
            }
            var sb = new StringBuilder();
            new Interpreter(sb).ExecBlock(node.Body, scope);
            return sb.ToString();
        }
    }

    private sealed class Interpreter(StringBuilder output)
    {
        public Flow ExecBlock(List<Node> nodes, Scope scope)
        {
            foreach (var n in nodes)
            {
                var f = Exec(n, scope);
                if (f != Flow.None) return f;
            }
            return Flow.None;
        }

        private Flow Exec(Node n, Scope scope)
        {
            switch (n)
            {
                case TextNode t: output.Append(t.Text); break;
                case OutputNode o: output.Append(Values.Str(Eval(o.E, scope))); break;
                case IfNode i:
                    foreach (var (cond, body) in i.Branches)
                        if (Values.Truthy(Eval(cond, scope))) return ExecBlock(body, scope);
                    if (i.Else != null) return ExecBlock(i.Else, scope);
                    break;
                case ForNode f: return ExecFor(f, scope);
                case SetNode s: ExecSet(s, scope); break;
                case MacroNode m:
                {
                    var mv = new MacroValue(m, scope, this);
                    scope.Set(m.Name, (JinjaCallable)((a, k) => mv.Call(a, k)));
                    break;
                }
                case FlowNode fl: return fl.IsBreak ? Flow.Break : Flow.Continue;
                case PassNode p: return ExecBlock(p.Body, scope);
                case DoNode d: Eval(d.E, scope); break;
            }
            return Flow.None;
        }

        private Flow ExecFor(ForNode f, Scope scope)
        {
            var seq = Values.Iterate(Eval(f.Iter, scope)).ToList();
            if (f.Filter != null)
            {
                var kept = new List<object?>();
                foreach (var item in seq)
                {
                    var probe = new Scope(scope); Bind(f.Targets, item, probe);
                    if (Values.Truthy(Eval(f.Filter, probe))) kept.Add(item);
                }
                seq = kept;
            }
            if (seq.Count == 0) { if (f.Else != null) return ExecBlock(f.Else, scope); return Flow.None; }
            for (int i = 0; i < seq.Count; i++)
            {
                var inner = new Scope(scope);
                Bind(f.Targets, seq[i], inner);
                var loop = new Dictionary<string, object?>
                {
                    ["index0"] = (long)i, ["index"] = (long)(i + 1), ["first"] = i == 0, ["last"] = i == seq.Count - 1,
                    ["length"] = (long)seq.Count, ["revindex"] = (long)(seq.Count - i), ["revindex0"] = (long)(seq.Count - i - 1),
                    ["previtem"] = i > 0 ? seq[i - 1] : JinjaUndefined.Instance, ["nextitem"] = i < seq.Count - 1 ? seq[i + 1] : JinjaUndefined.Instance,
                };
                inner.Set("loop", loop);
                var flow = ExecBlock(f.Body, inner);
                if (flow == Flow.Break) break;
            }
            return Flow.None;
        }

        private static void Bind(List<string> targets, object? item, Scope scope)
        {
            if (targets.Count == 1) { scope.Set(targets[0], item); return; }
            var parts = Values.Iterate(item).ToList();
            if (parts.Count != targets.Count) throw new JinjaException($"Cannot unpack {parts.Count} values into {targets.Count} names.");
            for (int i = 0; i < targets.Count; i++) scope.Set(targets[i], parts[i]);
        }

        private void ExecSet(SetNode s, Scope scope)
        {
            object? value;
            if (s.Block != null)
            {
                var sb = new StringBuilder(); new Interpreter(sb).ExecBlock(s.Block, new Scope(scope)); value = sb.ToString();
            }
            else value = Eval(s.Value!, scope);
            switch (s.Target)
            {
                case NameE n: scope.Set(n.Name, value); break;
                case AttrE a:
                    if (Eval(a.Obj, scope) is JinjaNamespace ns) ns[a.Name] = value;
                    else throw new JinjaException($"Cannot assign to attribute '{a.Name}' (only namespace objects allow it).");
                    break;
                case ListE l when l.Items.All(x => x is NameE):
                {
                    var parts = Values.Iterate(value).ToList();
                    for (int i = 0; i < l.Items.Count && i < parts.Count; i++) scope.Set(((NameE)l.Items[i]).Name, parts[i]);
                    break;
                }
                default: throw new JinjaException("Unsupported assignment target.");
            }
        }

        // ------------------------------------------------------------------ expressions

        public object? Eval(Expr e, Scope scope)
        {
            switch (e)
            {
                case Lit l: return l.V;
                case NameE n: return scope.TryGet(n.Name, out var v) ? v : JinjaUndefined.Instance;
                case AttrE a: return Values.GetAttr(Eval(a.Obj, scope), a.Name);
                case IndexE ix: return Values.GetItem(Eval(ix.Obj, scope), Eval(ix.Index, scope));
                case SliceE sl: return Values.Slice(Eval(sl.Obj, scope), sl.Start == null ? null : Eval(sl.Start, scope), sl.Stop == null ? null : Eval(sl.Stop, scope), sl.Step == null ? null : Eval(sl.Step, scope));
                case ListE li: return li.Items.Select(x => Eval(x, scope)).ToList();
                case DictE d: { var r = new Dictionary<string, object?>(); foreach (var (k, val) in d.Items) r[Values.Str(Eval(k, scope))] = Eval(val, scope); return r; }
                case UnaryE u:
                {
                    var x = Eval(u.E, scope);
                    return u.Op == "not" ? !Values.Truthy(x) : Values.Negate(x);
                }
                case BinaryE b: return EvalBinary(b, scope);
                case CondE c:
                    if (Values.Truthy(Eval(c.Cond, scope))) return Eval(c.Then, scope);
                    return c.Else != null ? Eval(c.Else, scope) : JinjaUndefined.Instance;
                case FilterE f:
                {
                    var args = f.Args.Select(x => Eval(x, scope)).ToList();
                    var kw = f.Kw.ToDictionary(kv => kv.Key, kv => Eval(kv.Value, scope));
                    return Filters.Apply(f.Name, Eval(f.Value, scope), args, kw, this, scope);
                }
                case TestE t:
                {
                    var r = Tests.Run(t.Name, Eval(t.Value, scope), t.Args.Select(x => Eval(x, scope)).ToList());
                    return t.Negate ? !r : r;
                }
                case CallE c: return EvalCall(c, scope);
            }
            throw new JinjaException("Unsupported expression.");
        }

        private object? EvalBinary(BinaryE b, Scope scope)
        {
            if (b.Op == "and") { var l = Eval(b.L, scope); return Values.Truthy(l) ? Eval(b.R, scope) : l; }
            if (b.Op == "or") { var l = Eval(b.L, scope); return Values.Truthy(l) ? l : Eval(b.R, scope); }
            var left = Eval(b.L, scope); var right = Eval(b.R, scope);
            switch (b.Op)
            {
                case "==": return Values.Equal(left, right);
                case "!=": return !Values.Equal(left, right);
                case "<": return Values.Compare(left, right) < 0;
                case ">": return Values.Compare(left, right) > 0;
                case "<=": return Values.Compare(left, right) <= 0;
                case ">=": return Values.Compare(left, right) >= 0;
                case "in": return Values.Contains(right, left);
                case "~": return Values.Str(left) + Values.Str(right);
                default: return Values.Arithmetic(b.Op, left, right);
            }
        }

        private object? EvalCall(CallE c, Scope scope)
        {
            var args = c.Args.Select(x => Eval(x, scope)).ToList();
            var kw = c.Kw.ToDictionary(kv => kv.Key, kv => Eval(kv.Value, scope));
            if (c.Fn is Lit { V: null }) return null;                                // {% do expr %}
            if (c.Fn is AttrE attr)
            {
                var obj = Eval(attr.Obj, scope);
                // a callable stored in a dict / namespace wins over a method of the same name
                if (obj is Dictionary<string, object?> d && d.TryGetValue(attr.Name, out var stored) && stored is JinjaCallable cb) return cb(args, kw);
                return Methods.Call(obj, attr.Name, args, kw);
            }
            var fn = Eval(c.Fn, scope);
            if (fn is JinjaCallable call) return call(args, kw);
            throw new JinjaException(c.Fn is NameE ne ? $"'{ne.Name}' is not a function." : "Value is not callable.");
        }
    }

    // ====================================================================== values

    private static class Values
    {
        public static bool Truthy(object? v) => v switch
        {
            null => false, JinjaUndefined => false, bool b => b, long l => l != 0, double d => d != 0, string s => s.Length > 0,
            List<object?> l => l.Count > 0, Dictionary<string, object?> d => d.Count > 0, _ => true,
        };

        public static bool IsNumber(object? v) => v is long or double or int;
        public static double ToDouble(object? v) => v switch { long l => l, int i => i, double d => d, bool b => b ? 1 : 0, _ => throw new JinjaException("A number was expected.") };

        public static string Str(object? v) => v switch
        {
            null => "None", JinjaUndefined => "", bool b => b ? "True" : "False", string s => s, long l => l.ToString(CultureInfo.InvariantCulture),
            double d => FormatDouble(d), _ => Repr(v),
        };

        public static string FormatDouble(double d)
        {
            if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            return s.Contains('.') || s.Contains('E') || s.Contains('N') ? s : s + ".0";
        }

        /// <summary>Python-style repr (what a template prints for a list or dict).</summary>
        public static string Repr(object? v)
        {
            switch (v)
            {
                case string s: return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n") + "'";
                case List<object?> l: return "[" + string.Join(", ", l.Select(Repr)) + "]";
                case Dictionary<string, object?> d: return "{" + string.Join(", ", d.Select(kv => Repr(kv.Key) + ": " + Repr(kv.Value))) + "}";
                case JinjaCallable: return "<function>";
                default: return Str(v);
            }
        }

        public static IEnumerable<object?> Iterate(object? v) => v switch
        {
            null => throw new JinjaException("None is not iterable."),
            JinjaUndefined => Array.Empty<object?>(),
            List<object?> l => l,
            string s => s.Select(c => (object?)c.ToString()),
            Dictionary<string, object?> d => d.Keys.Select(k => (object?)k),
            _ => throw new JinjaException("Value is not iterable."),
        };

        public static object? GetAttr(object? o, string name)
        {
            switch (o)
            {
                case Dictionary<string, object?> d: return d.TryGetValue(name, out var v) ? v : JinjaUndefined.Instance;
                case null: case JinjaUndefined: return JinjaUndefined.Instance;
                default: return JinjaUndefined.Instance;
            }
        }

        public static object? GetItem(object? o, object? key)
        {
            switch (o)
            {
                case List<object?> l when IsNumber(key):
                {
                    long i = (long)ToDouble(key); if (i < 0) i += l.Count;
                    return i >= 0 && i < l.Count ? l[(int)i] : JinjaUndefined.Instance;
                }
                case string s when IsNumber(key):
                {
                    long i = (long)ToDouble(key); if (i < 0) i += s.Length;
                    return i >= 0 && i < s.Length ? s[(int)i].ToString() : JinjaUndefined.Instance;
                }
                case Dictionary<string, object?> d: return d.TryGetValue(Str(key), out var v) ? v : JinjaUndefined.Instance;
                default: return JinjaUndefined.Instance;
            }
        }

        public static object? Slice(object? o, object? a, object? b, object? c)
        {
            int count = o switch { List<object?> l => l.Count, string s => s.Length, JinjaUndefined => 0, _ => throw new JinjaException("Only lists and strings can be sliced.") };
            long step = c == null ? 1 : (long)ToDouble(c);
            if (step == 0) throw new JinjaException("Slice step cannot be zero.");
            long start, stop;
            if (step > 0)
            {
                start = a == null ? 0 : Clamp((long)ToDouble(a), count, 0, count);
                stop = b == null ? count : Clamp((long)ToDouble(b), count, 0, count);
            }
            else
            {
                start = a == null ? count - 1 : Clamp((long)ToDouble(a), count, -1, count - 1);
                stop = b == null ? -1 : Clamp((long)ToDouble(b), count, -1, count - 1);
            }
            var idx = new List<int>();
            for (long i = start; step > 0 ? i < stop : i > stop; i += step) idx.Add((int)i);
            if (o is string str) return new string(idx.Select(i => str[i]).ToArray());
            if (o is List<object?> list) return idx.Select(i => list[i]).ToList();
            return new List<object?>();
        }

        private static long Clamp(long i, int count, long lo, long hi)
        {
            if (i < 0) i += count;
            return Math.Max(lo, Math.Min(hi, i));
        }

        public static bool Equal(object? a, object? b)
        {
            if (a is JinjaUndefined && b is JinjaUndefined) return true;
            if (IsNumber(a) && IsNumber(b)) return ToDouble(a) == ToDouble(b);
            if (a is bool ba && IsNumber(b)) return (ba ? 1 : 0) == ToDouble(b);
            if (b is bool bb && IsNumber(a)) return (bb ? 1 : 0) == ToDouble(a);
            if (a is List<object?> la && b is List<object?> lb) return la.Count == lb.Count && la.Zip(lb).All(p => Equal(p.First, p.Second));
            if (a is Dictionary<string, object?> da && b is Dictionary<string, object?> db)
                return da.Count == db.Count && da.All(kv => db.TryGetValue(kv.Key, out var o) && Equal(kv.Value, o));
            return Equals(a, b);
        }

        public static int Compare(object? a, object? b)
        {
            if (IsNumber(a) && IsNumber(b)) return ToDouble(a).CompareTo(ToDouble(b));
            if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
            throw new JinjaException($"Cannot compare {TypeName(a)} with {TypeName(b)}.");
        }

        public static string TypeName(object? v) => v switch { null => "None", string => "string", bool => "bool", long or int => "int", double => "float", List<object?> => "list", Dictionary<string, object?> => "dict", _ => "value" };

        public static bool Contains(object? container, object? item) => container switch
        {
            string s => s.Contains(Str(item), StringComparison.Ordinal),
            List<object?> l => l.Any(x => Equal(x, item)),
            Dictionary<string, object?> d => d.ContainsKey(Str(item)),
            JinjaUndefined => false,
            _ => throw new JinjaException("The right side of 'in' must be a string, list or dict."),
        };

        public static object? Negate(object? v) => v switch { long l => -l, double d => -d, int i => -(long)i, _ => throw new JinjaException("A number was expected after '-'.") };

        public static object? Arithmetic(string op, object? a, object? b)
        {
            if (op == "+" && a is string sa && b is string sb) return sa + sb;
            if (op == "+" && a is List<object?> la && b is List<object?> lb) return la.Concat(lb).ToList();
            if (op == "*" && a is string s && IsNumber(b)) return string.Concat(Enumerable.Repeat(s, (int)Math.Max(0, ToDouble(b))));
            if (!IsNumber(a) || !IsNumber(b)) throw new JinjaException($"Unsupported operands for '{op}': {TypeName(a)} and {TypeName(b)}.");
            bool ints = a is long or int && b is long or int;
            double x = ToDouble(a), y = ToDouble(b);
            switch (op)
            {
                case "+": return ints ? (long)x + (long)y : x + y;
                case "-": return ints ? (long)x - (long)y : x - y;
                case "*": return ints ? (long)x * (long)y : x * y;
                case "/": return x / y;
                case "//": return ints ? (long)Math.Floor(x / y) : Math.Floor(x / y);
                case "%": return ints ? (((long)x % (long)y) + (long)y) % (long)y : x - Math.Floor(x / y) * y;
                case "**": return ints && y >= 0 ? (long)Math.Pow(x, y) : Math.Pow(x, y);
            }
            throw new JinjaException($"Unsupported operator '{op}'.");
        }
    }

    // ====================================================================== tests ("x is defined")

    private static class Tests
    {
        public static bool Run(string name, object? v, List<object?> args)
        {
            switch (name)
            {
                case "defined": return v is not JinjaUndefined;
                case "undefined": return v is JinjaUndefined;
                case "none": return v is null;
                case "string": return v is string;
                case "number": return Values.IsNumber(v);
                case "integer": return v is long or int;
                case "float": return v is double;
                case "boolean": return v is bool;
                case "true": return v is true;
                case "false": return v is false;
                case "mapping": return v is Dictionary<string, object?>;
                case "sequence": case "iterable": return v is List<object?> or string or Dictionary<string, object?>;
                case "callable": return v is JinjaCallable;
                case "odd": return Values.IsNumber(v) && (long)Values.ToDouble(v) % 2 != 0;
                case "even": return Values.IsNumber(v) && (long)Values.ToDouble(v) % 2 == 0;
                case "divisibleby": return Values.IsNumber(v) && (long)Values.ToDouble(v) % (long)Values.ToDouble(args[0]) == 0;
                case "lower": return v is string l && l == l.ToLowerInvariant();
                case "upper": return v is string u && u == u.ToUpperInvariant();
                case "eq": case "equalto": case "==": return Values.Equal(v, args[0]);
                case "ne": case "!=": return !Values.Equal(v, args[0]);
                case "lt": case "<": return Values.Compare(v, args[0]) < 0;
                case "le": case "<=": return Values.Compare(v, args[0]) <= 0;
                case "gt": case ">": return Values.Compare(v, args[0]) > 0;
                case "ge": case ">=": return Values.Compare(v, args[0]) >= 0;
                case "in": return Values.Contains(args[0], v);
                default: throw new JinjaException($"Unknown test '{name}'.");
            }
        }
    }

    // ====================================================================== methods ("x.strip()")

    private static class Methods
    {
        private static object? Arg(List<object?> a, int i, object? def = null) => i < a.Count ? a[i] : def;

        public static object? Call(object? o, string name, List<object?> a, Dictionary<string, object?> kw)
        {
            switch (o)
            {
                case string s: return CallString(s, name, a, kw);
                case List<object?> l: return CallList(l, name, a);
                case Dictionary<string, object?> d: return CallDict(d, name, a);
                case JinjaUndefined: throw new JinjaException($"Cannot call '{name}' on an undefined value.");
                case null: throw new JinjaException($"Cannot call '{name}' on None.");
            }
            throw new JinjaException($"Unknown method '{name}'.");
        }

        private static object? CallString(string s, string name, List<object?> a, Dictionary<string, object?> kw)
        {
            string Chars() => Arg(a, 0) is string c ? c : null!;
            switch (name)
            {
                case "strip": return Arg(a, 0) is string cs ? s.Trim(cs.ToCharArray()) : s.Trim();
                case "lstrip": return Arg(a, 0) is string cl ? s.TrimStart(cl.ToCharArray()) : s.TrimStart();
                case "rstrip": return Arg(a, 0) is string cr ? s.TrimEnd(cr.ToCharArray()) : s.TrimEnd();
                case "upper": return s.ToUpperInvariant();
                case "lower": return s.ToLowerInvariant();
                case "title": return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
                case "capitalize": return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
                case "startswith": return Prefixes(a).Any(p => s.StartsWith(p, StringComparison.Ordinal));
                case "endswith": return Prefixes(a).Any(p => s.EndsWith(p, StringComparison.Ordinal));
                case "replace":
                {
                    string from = Values.Str(Arg(a, 0)), to = Values.Str(Arg(a, 1));
                    if (Arg(a, 2) is { } cnt && Values.IsNumber(cnt) && (long)Values.ToDouble(cnt) >= 0) return ReplaceN(s, from, to, (int)Values.ToDouble(cnt));
                    return from.Length == 0 ? s : s.Replace(from, to, StringComparison.Ordinal);
                }
                case "split":
                case "rsplit":
                {
                    int max = Arg(a, 1) is { } m && Values.IsNumber(m) ? (int)Values.ToDouble(m) : -1;
                    if (Arg(a, 0) is not string sep || sep.Length == 0)
                        return s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(x => (object?)x).ToList();
                    if (max < 0) return s.Split(sep, StringSplitOptions.None).Select(x => (object?)x).ToList();
                    if (name == "split") return s.Split(sep, max + 1, StringSplitOptions.None).Select(x => (object?)x).ToList();
                    var parts = new List<object?>(); string rest = s;
                    for (int i = 0; i < max; i++)
                    {
                        int at = rest.LastIndexOf(sep, StringComparison.Ordinal);
                        if (at < 0) break;
                        parts.Insert(0, rest[(at + sep.Length)..]); rest = rest[..at];
                    }
                    parts.Insert(0, rest);
                    return parts;
                }
                case "splitlines": return s.Split('\n').Select(x => (object?)x.TrimEnd('\r')).ToList();
                case "join": return string.Join(s, Values.Iterate(Arg(a, 0)).Select(Values.Str));
                case "find": return (long)s.IndexOf(Values.Str(Arg(a, 0)), StringComparison.Ordinal);
                case "rfind": return (long)s.LastIndexOf(Values.Str(Arg(a, 0)), StringComparison.Ordinal);
                case "count": { string sub = Values.Str(Arg(a, 0)); if (sub.Length == 0) return (long)s.Length + 1; int n = 0, at = 0; while ((at = s.IndexOf(sub, at, StringComparison.Ordinal)) >= 0) { n++; at += sub.Length; } return (long)n; }
                case "isdigit": return s.Length > 0 && s.All(char.IsDigit);
                case "isalpha": return s.Length > 0 && s.All(char.IsLetter);
                case "isspace": return s.Length > 0 && s.All(char.IsWhiteSpace);
                case "zfill": return s.PadLeft((int)Values.ToDouble(Arg(a, 0)), '0');
                case "format": return string.Format(CultureInfo.InvariantCulture, Regex.Replace(s, @"\{(\d*)\}", m => "{" + (m.Groups[1].Value.Length == 0 ? "0" : m.Groups[1].Value) + "}"), a.Select(Values.Str).ToArray<object>());
            }
            throw new JinjaException($"Unknown string method '{name}'.");
        }

        private static IEnumerable<string> Prefixes(List<object?> a) =>
            a.Count > 0 && a[0] is List<object?> l ? l.Select(Values.Str) : a.Take(1).Select(Values.Str);

        private static string ReplaceN(string s, string from, string to, int n)
        {
            if (from.Length == 0 || n <= 0) return s;
            var sb = new StringBuilder(); int at = 0;
            while (n-- > 0)
            {
                int i = s.IndexOf(from, at, StringComparison.Ordinal);
                if (i < 0) break;
                sb.Append(s, at, i - at).Append(to); at = i + from.Length;
            }
            return sb.Append(s, at, s.Length - at).ToString();
        }

        private static object? CallList(List<object?> l, string name, List<object?> a)
        {
            switch (name)
            {
                case "append": l.Add(Arg(a, 0)); return null;
                case "extend": l.AddRange(Values.Iterate(Arg(a, 0))); return null;
                case "pop": { if (l.Count == 0) throw new JinjaException("pop from empty list"); int i = a.Count > 0 ? (int)Values.ToDouble(a[0]) : l.Count - 1; if (i < 0) i += l.Count; var v = l[i]; l.RemoveAt(i); return v; }
                case "insert": l.Insert((int)Values.ToDouble(Arg(a, 0)), Arg(a, 1)); return null;
                case "index": { int i = l.FindIndex(x => Values.Equal(x, Arg(a, 0))); if (i < 0) throw new JinjaException("value not in list"); return (long)i; }
                case "count": return (long)l.Count(x => Values.Equal(x, Arg(a, 0)));
                case "reverse": l.Reverse(); return null;
            }
            throw new JinjaException($"Unknown list method '{name}'.");
        }

        private static object? CallDict(Dictionary<string, object?> d, string name, List<object?> a)
        {
            switch (name)
            {
                case "get": return d.TryGetValue(Values.Str(Arg(a, 0)), out var v) ? v : Arg(a, 1);
                case "items": return d.Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList();
                case "keys": return d.Keys.Select(k => (object?)k).ToList();
                case "values": return d.Values.ToList();
                case "update": if (Arg(a, 0) is Dictionary<string, object?> o) foreach (var kv in o) d[kv.Key] = kv.Value; return null;
                case "pop": { string k = Values.Str(Arg(a, 0)); if (d.Remove(k, out var pv)) return pv; return Arg(a, 1); }
                case "setdefault": { string k = Values.Str(Arg(a, 0)); if (!d.ContainsKey(k)) d[k] = Arg(a, 1); return d[k]; }
                case "copy": return new Dictionary<string, object?>(d);
            }
            throw new JinjaException($"Unknown dict method '{name}'.");
        }
    }

    // ====================================================================== filters ("x | trim")

    private static class Filters
    {
        public static object? Apply(string name, object? v, List<object?> a, Dictionary<string, object?> kw, Interpreter interp, Scope scope)
        {
            object? Arg(int i, string? k = null, object? def = null) => i < a.Count ? a[i] : k != null && kw.TryGetValue(k, out var x) ? x : def;
            switch (name)
            {
                case "trim": case "strip": return Values.Str(v).Trim();
                case "upper": return Values.Str(v).ToUpperInvariant();
                case "lower": return Values.Str(v).ToLowerInvariant();
                case "title": return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Values.Str(v).ToLowerInvariant());
                case "capitalize": { var s = Values.Str(v); return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant(); }
                case "length": case "count": return v switch { string s => (long)s.Length, List<object?> l => (long)l.Count, Dictionary<string, object?> d => (long)d.Count, JinjaUndefined => 0L, _ => throw new JinjaException("length() needs a string, list or dict.") };
                case "string": return Values.Str(v);
                case "int": return v switch { long l => l, double d => (long)d, bool b => b ? 1L : 0L, string s when long.TryParse(s.Trim(), out var n) => n, _ => Arg(0, "default", 0L) };
                case "float": return v switch { long l => (double)l, double d => d, string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n, _ => Arg(0, "default", 0.0) };
                case "abs": return v switch { long l => Math.Abs(l), double d => Math.Abs(d), _ => throw new JinjaException("abs() needs a number.") };
                case "round": return Math.Round(Values.ToDouble(v), a.Count > 0 ? (int)Values.ToDouble(a[0]) : 0, MidpointRounding.ToEven);
                case "list": return Values.Iterate(v).ToList();
                case "first": return Values.Iterate(v).FirstOrDefault() ?? (v is JinjaUndefined ? v : null);
                case "last": { var l = Values.Iterate(v).ToList(); return l.Count == 0 ? JinjaUndefined.Instance : l[^1]; }
                case "reverse": return v is string rs ? new string(rs.Reverse().ToArray()) : Values.Iterate(v).Reverse().ToList();
                case "join":
                {
                    string sep = Values.Str(Arg(0, "d", ""));
                    var items = Values.Iterate(v);
                    if (Arg(1, "attribute") is { } attr) items = items.Select(x => Values.GetAttr(x, Values.Str(attr)));
                    return string.Join(sep, items.Select(Values.Str));
                }
                case "default": case "d":
                {
                    object? def = Arg(0, "default_value", ""); bool boolean = Arg(1, "boolean") is true;
                    return v is JinjaUndefined || (boolean && !Values.Truthy(v)) ? def : v;
                }
                case "replace": return Methods.Call(Values.Str(v), "replace", a, kw);
                case "safe": case "e": case "escape": case "forceescape": return v;
                case "tojson": return Json.Serialize(v, Arg(0, "indent") is { } ind && Values.IsNumber(ind) ? (int)Values.ToDouble(ind) : (int?)null, Arg(1, "sort_keys") is true);
                case "indent":
                {
                    int width = Values.IsNumber(Arg(0, "width")) ? (int)Values.ToDouble(Arg(0, "width")) : 4; bool first = Arg(1, "first") is true;
                    var lines = Values.Str(v).Split('\n');
                    return string.Join("\n", lines.Select((ln, i) => (i == 0 && !first) || ln.Length == 0 ? ln : new string(' ', width) + ln));
                }
                case "items": return v is Dictionary<string, object?> di ? di.Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList() : new List<object?>();
                case "dictsort": return v is Dictionary<string, object?> dd ? dd.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (object?)new List<object?> { kv.Key, kv.Value }).ToList() : new List<object?>();
                case "sort":
                {
                    var items = Values.Iterate(v).ToList(); bool rev = Arg(0, "reverse") is true;
                    object? Key(object? x) => Arg(2, "attribute") is { } at ? Values.GetAttr(x, Values.Str(at)) : x;
                    items.Sort((x, y) => Values.Compare(Key(x), Key(y)));
                    if (rev) items.Reverse();
                    return items;
                }
                case "unique": { var seen = new List<object?>(); foreach (var x in Values.Iterate(v)) if (!seen.Any(y => Values.Equal(x, y))) seen.Add(x); return seen; }
                case "sum": return Values.Iterate(v).Aggregate((object?)0L, (acc, x) => Values.Arithmetic("+", acc, x));
                case "min": return Values.Iterate(v).Aggregate((x, y) => Values.Compare(x, y) <= 0 ? x : y);
                case "max": return Values.Iterate(v).Aggregate((x, y) => Values.Compare(x, y) >= 0 ? x : y);
                case "map":
                {
                    if (kw.TryGetValue("attribute", out var attr)) return Values.Iterate(v).Select(x => Values.GetAttr(x, Values.Str(attr))).ToList();
                    string fname = Values.Str(Arg(0));
                    return Values.Iterate(v).Select(x => Apply(fname, x, a.Skip(1).ToList(), kw, interp, scope)).ToList();
                }
                case "select": case "reject":
                {
                    bool want = name == "select"; string test = a.Count > 0 ? Values.Str(a[0]) : "";
                    return Values.Iterate(v).Where(x => (test.Length == 0 ? Values.Truthy(x) : Tests.Run(test, x, a.Skip(1).ToList())) == want).ToList();
                }
                case "selectattr": case "rejectattr":
                {
                    bool want = name == "selectattr"; string attr = Values.Str(Arg(0));
                    string test = a.Count > 1 ? Values.Str(a[1]) : "";
                    return Values.Iterate(v).Where(x =>
                    {
                        var val = Values.GetAttr(x, attr);
                        return (test.Length == 0 ? Values.Truthy(val) : Tests.Run(test, val, a.Skip(2).ToList())) == want;
                    }).ToList();
                }
                case "truncate": { var s = Values.Str(v); int len = a.Count > 0 ? (int)Values.ToDouble(a[0]) : 255; return s.Length <= len ? s : s[..Math.Max(0, len - 3)] + "..."; }
                case "wordcount": return (long)Values.Str(v).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                case "batch": { int n = (int)Values.ToDouble(Arg(0)); var all = Values.Iterate(v).ToList(); return Enumerable.Range(0, (all.Count + n - 1) / n).Select(i => (object?)all.Skip(i * n).Take(n).ToList()).ToList(); }
            }
            throw new JinjaException($"Unknown filter '{name}'.");
        }
    }

    // ====================================================================== JSON (tojson)

    private static class Json
    {
        public static string Serialize(object? v, int? indent, bool sortKeys)
        {
            var sb = new StringBuilder();
            Write(sb, v, indent, sortKeys, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object? v, int? indent, bool sort, int level)
        {
            switch (v)
            {
                case null: case JinjaUndefined: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(Values.FormatDouble(d)); break;
                case string s: WriteString(sb, s); break;
                case List<object?> list:
                    if (list.Count == 0) { sb.Append("[]"); break; }
                    sb.Append('[');
                    for (int k = 0; k < list.Count; k++)
                    {
                        if (k > 0) sb.Append(indent.HasValue ? "," : ", ");
                        NewLine(sb, indent, level + 1);
                        Write(sb, list[k], indent, sort, level + 1);
                    }
                    NewLine(sb, indent, level); sb.Append(']');
                    break;
                case Dictionary<string, object?> dict:
                    if (dict.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    int n = 0;
                    foreach (var kv in sort ? dict.OrderBy(x => x.Key, StringComparer.Ordinal) : dict.AsEnumerable())
                    {
                        if (n++ > 0) sb.Append(indent.HasValue ? "," : ", ");
                        NewLine(sb, indent, level + 1);
                        WriteString(sb, kv.Key); sb.Append(": ");
                        Write(sb, kv.Value, indent, sort, level + 1);
                    }
                    NewLine(sb, indent, level); sb.Append('}');
                    break;
                default: WriteString(sb, v.ToString() ?? ""); break;
            }
        }

        private static void NewLine(StringBuilder sb, int? indent, int level)
        {
            if (indent.HasValue) sb.Append('\n').Append(' ', indent.Value * level);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
    }

    // ====================================================================== built-in functions

    private static class Builtins
    {
        public static readonly Dictionary<string, object?> Globals = new()
        {
            ["raise_exception"] = (JinjaCallable)((a, k) => throw new JinjaException(a.Count > 0 ? Values.Str(a[0]) : "Template raised an exception.")),
            ["namespace"] = (JinjaCallable)((a, k) =>
            {
                var ns = new JinjaNamespace();
                if (a.Count > 0 && a[0] is Dictionary<string, object?> init) foreach (var kv in init) ns[kv.Key] = kv.Value;
                foreach (var kv in k) ns[kv.Key] = kv.Value;
                return ns;
            }),
            ["range"] = (JinjaCallable)((a, k) =>
            {
                long start = 0, stop, step = 1;
                if (a.Count == 1) stop = (long)Values.ToDouble(a[0]);
                else { start = (long)Values.ToDouble(a[0]); stop = (long)Values.ToDouble(a[1]); if (a.Count > 2) step = (long)Values.ToDouble(a[2]); }
                if (step == 0) throw new JinjaException("range() step cannot be zero.");
                var r = new List<object?>();
                for (long i = start; step > 0 ? i < stop : i > stop; i += step) r.Add(i);
                return r;
            }),
            ["dict"] = (JinjaCallable)((a, k) =>
            {
                var d = new Dictionary<string, object?>();
                if (a.Count > 0 && a[0] is Dictionary<string, object?> src) foreach (var kv in src) d[kv.Key] = kv.Value;
                foreach (var kv in k) d[kv.Key] = kv.Value;
                return d;
            }),
            ["strftime_now"] = (JinjaCallable)((a, k) => Strftime(a.Count > 0 ? Values.Str(a[0]) : "%Y-%m-%d", DateTime.Now)),
            ["lipsum"] = (JinjaCallable)((a, k) => "Lorem ipsum dolor sit amet"),
        };

        /// <summary>Python's strftime directives that chat templates use (date in the system prompt).</summary>
        public static string Strftime(string fmt, DateTime t)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < fmt.Length; i++)
            {
                if (fmt[i] != '%' || i + 1 >= fmt.Length) { sb.Append(fmt[i]); continue; }
                char c = fmt[++i];
                bool strip = false;
                if (c == '-' && i + 1 < fmt.Length) { strip = true; c = fmt[++i]; }
                string piece = c switch
                {
                    'Y' => t.ToString("yyyy", ci), 'y' => t.ToString("yy", ci), 'm' => t.ToString("MM", ci), 'd' => t.ToString("dd", ci),
                    'B' => t.ToString("MMMM", ci), 'b' => t.ToString("MMM", ci), 'A' => t.ToString("dddd", ci), 'a' => t.ToString("ddd", ci),
                    'H' => t.ToString("HH", ci), 'I' => t.ToString("hh", ci), 'M' => t.ToString("mm", ci), 'S' => t.ToString("ss", ci),
                    'p' => t.ToString("tt", ci), 'j' => t.DayOfYear.ToString("000"), 'w' => ((int)t.DayOfWeek).ToString(), 'e' => t.Day.ToString().PadLeft(2),
                    'Z' => "UTC", 'z' => t.ToString("zzz", ci).Replace(":", ""), '%' => "%", 'F' => t.ToString("yyyy-MM-dd", ci), 'T' => t.ToString("HH:mm:ss", ci),
                    _ => "%" + c,
                };
                sb.Append(strip ? piece.TrimStart('0', ' ') is { Length: > 0 } z ? z : "0" : piece);
            }
            return sb.ToString();
        }
    }
}
