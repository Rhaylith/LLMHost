using LLMHostProject.Inference;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class JinjaTests
{
    private static Dictionary<string, object?> Msg(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static string Render(string template, params (string Key, object? Value)[] vars)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in vars) d[k] = v;
        return new JinjaTemplate(template).Render(d);
    }

    private static List<object?> Messages(params (string Role, string Content)[] m) => m.Select(x => (object?)Msg(x.Role, x.Content)).ToList();

    // ---------------------------------------------------------------- the chat-template families

    [Fact]
    public void ChatMl()
    {
        const string t = "{% for message in messages %}{{'<|im_start|>' + message['role'] + '\\n' + message['content'] + '<|im_end|>' + '\\n'}}{% endfor %}{% if add_generation_prompt %}{{ '<|im_start|>assistant\\n' }}{% endif %}";
        string r = Render(t, ("messages", Messages(("system", "S"), ("user", "hi"))), ("add_generation_prompt", true));
        Assert.Equal("<|im_start|>system\nS<|im_end|>\n<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\n", r);
    }

    [Fact]
    public void Llama3()
    {
        const string t = "{% set loop_messages = messages %}{% for message in loop_messages %}{% set content = '<|start_header_id|>' + message['role'] + '<|end_header_id|>\\n\\n'+ message['content'] | trim + '<|eot_id|>' %}{% if loop.index0 == 0 %}{% set content = bos_token + content %}{% endif %}{{ content }}{% endfor %}{% if add_generation_prompt %}{{ '<|start_header_id|>assistant<|end_header_id|>\\n\\n' }}{% endif %}";
        string r = Render(t, ("messages", Messages(("user", "  hi  "))), ("add_generation_prompt", true), ("bos_token", "<|begin_of_text|>"));
        Assert.Equal("<|begin_of_text|><|start_header_id|>user<|end_header_id|>\n\nhi<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n", r);
    }

    private const string Gemma = "{{ bos_token }}{% if messages[0]['role'] == 'system' %}{{ raise_exception('System role not supported') }}{% endif %}{% for message in messages %}{% if (message['role'] == 'user') != (loop.index0 % 2 == 0) %}{{ raise_exception('Conversation roles must alternate user/assistant/user/assistant/...') }}{% endif %}{% if (message['role'] == 'assistant') %}{% set role = 'model' %}{% else %}{% set role = message['role'] %}{% endif %}{{ '<start_of_turn>' + role + '\\n' + message['content'] | trim + '<end_of_turn>\\n' }}{% endfor %}{% if add_generation_prompt %}{{'<start_of_turn>model\\n'}}{% endif %}";

    [Fact]
    public void GemmaAlternatingRolesAndRaiseException()
    {
        string ok = Render(Gemma, ("messages", Messages(("user", "Q"), ("assistant", "A"), ("user", "Q2"))), ("add_generation_prompt", true), ("bos_token", "<bos>"));
        Assert.Equal("<bos><start_of_turn>user\nQ<end_of_turn>\n<start_of_turn>model\nA<end_of_turn>\n<start_of_turn>user\nQ2<end_of_turn>\n<start_of_turn>model\n", ok);

        var ex = Assert.Throws<JinjaException>(() => Render(Gemma, ("messages", Messages(("system", "S"), ("user", "Q"))), ("bos_token", "<bos>")));
        Assert.Contains("System role not supported", ex.Message);
        Assert.Throws<JinjaException>(() => Render(Gemma, ("messages", Messages(("user", "Q"), ("user", "Q2"))), ("bos_token", "<bos>")));
    }

    [Fact]
    public void Mistral()
    {
        const string t = "{{ bos_token }}{% for message in messages %}{% if (message['role'] == 'user') != (loop.index0 % 2 == 0) %}{{ raise_exception('Conversation roles must alternate user/assistant/user/assistant/...') }}{% endif %}{% if message['role'] == 'user' %}{{ '[INST] ' + message['content'] + ' [/INST]' }}{% elif message['role'] == 'assistant' %}{{ message['content'] + eos_token}}{% else %}{{ raise_exception('Only user and assistant roles are supported!') }}{% endif %}{% endfor %}";
        string r = Render(t, ("messages", Messages(("user", "Q"), ("assistant", "A"), ("user", "Q2"))), ("bos_token", "<s>"), ("eos_token", "</s>"));
        Assert.Equal("<s>[INST] Q [/INST]A</s>[INST] Q2 [/INST]", r);
    }

    internal const string Qwen3 = """
        {%- set ns = namespace(multi_step_tool=true, last_query_index=messages|length - 1) %}
        {%- for message in messages[::-1] %}
            {%- set index = (messages|length - 1) - loop.index0 %}
            {%- if ns.multi_step_tool and message.role == "user" and message.content is string and not(message.content.startswith('<tool_response>') and message.content.endswith('</tool_response>')) %}
                {%- set ns.multi_step_tool = false %}
                {%- set ns.last_query_index = index %}
            {%- endif %}
        {%- endfor %}
        {%- for message in messages %}
            {%- if message.content is string %}
                {%- set content = message.content %}
            {%- else %}
                {%- set content = '' %}
            {%- endif %}
            {%- if (message.role == "user") or (message.role == "system" and not loop.first) %}
                {{- '<|im_start|>' + message.role + '\n' + content + '<|im_end|>' + '\n' }}
            {%- elif message.role == "assistant" %}
                {%- set reasoning_content = '' %}
                {%- if message.reasoning_content is string %}
                    {%- set reasoning_content = message.reasoning_content %}
                {%- else %}
                    {%- if '</think>' in content %}
                        {%- set reasoning_content = content.split('</think>')[0].rstrip('\n').split('<think>')[-1].lstrip('\n') %}
                        {%- set content = content.split('</think>')[-1].lstrip('\n') %}
                    {%- endif %}
                {%- endif %}
                {%- if loop.index0 > ns.last_query_index %}
                    {%- if loop.last or (not loop.last and reasoning_content) %}
                        {{- '<|im_start|>' + message.role + '\n<think>\n' + reasoning_content.strip('\n') + '\n</think>\n\n' + content.lstrip('\n') }}
                    {%- else %}
                        {{- '<|im_start|>' + message.role + '\n' + content }}
                    {%- endif %}
                {%- else %}
                    {{- '<|im_start|>' + message.role + '\n' + content }}
                {%- endif %}
                {{- '<|im_end|>\n' }}
            {%- endif %}
        {%- endfor %}
        {%- if add_generation_prompt %}
            {{- '<|im_start|>assistant\n' }}
            {%- if enable_thinking is defined and enable_thinking is false %}
                {{- '<think>\n\n</think>\n\n' }}
            {%- endif %}
        {%- endif %}
        """;

    [Fact]
    public void Qwen3DropsOldReasoningAndSwitchesThinkingOff()
    {
        var msgs = Messages(("user", "hi"), ("assistant", "<think>\nplan\n</think>\n\nanswer"), ("user", "again"));
        string off = Render(Qwen3, ("messages", msgs), ("add_generation_prompt", true), ("enable_thinking", false));
        Assert.Equal("<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\nanswer<|im_end|>\n<|im_start|>user\nagain<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n", off);

        string on = Render(Qwen3, ("messages", msgs), ("add_generation_prompt", true));
        Assert.EndsWith("<|im_start|>user\nagain<|im_end|>\n<|im_start|>assistant\n", on);       // enable_thinking undefined: no empty think block
    }

    [Fact]
    public void Qwen3KeepsReasoningOfTheTurnAfterTheLastQuestion()
    {
        var msgs = Messages(("user", "hi"), ("assistant", "<think>\nplan\n</think>\n\nanswer"));
        string r = Render(Qwen3, ("messages", msgs), ("add_generation_prompt", false));
        Assert.Equal("<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\n<think>\nplan\n</think>\n\nanswer<|im_end|>\n", r);
    }

    // ---------------------------------------------------------------- language features

    [Fact]
    public void TrimBlocksAndLstripBlocksMatchHuggingFace()
    {
        // the newline after a block tag and the indentation before one disappear; {{ }} lines keep theirs
        string r = Render("a\n    {% if true %}\n    b\n    {% endif %}\nc {{ 1 }}\n");
        Assert.Equal("a\n    b\nc 1\n", r);
    }

    [Fact]
    public void MinusMarkersStripWhitespaceOnTheirSide()
    {
        Assert.Equal("a-b", Render("a  {{- '-' -}}  b"));
        Assert.Equal("ab", Render("a\n  {%- if true -%}\n  b\n{%- endif %}"));
    }

    [Fact]
    public void NamespaceCanBeUpdatedInsideLoops()
    {
        string r = Render("{% set ns = namespace(n=0, s='') %}{% for x in xs %}{% set ns.n = ns.n + x %}{% set ns.s = ns.s ~ x %}{% endfor %}{{ ns.n }}/{{ ns.s }}",
            ("xs", new List<object?> { 1L, 2L, 3L }));
        Assert.Equal("6/123", r);
    }

    [Fact]
    public void LoopVariablesBreakContinueAndElse()
    {
        var xs = new List<object?> { "a", "b", "c", "d" };
        Assert.Equal("0a 1b 2c 3d ", Render("{% for x in xs %}{{ loop.index0 }}{{ x }} {% endfor %}", ("xs", xs)));
        Assert.Equal("a,b,c", Render("{% for x in xs %}{{ x }}{% if not loop.last %},{% endif %}{% endfor %}", ("xs", xs.Take(3).ToList())));
        Assert.Equal("ac", Render("{% for x in xs %}{% if x == 'b' %}{% continue %}{% endif %}{% if x == 'd' %}{% break %}{% endif %}{{ x }}{% endfor %}", ("xs", xs)));
        Assert.Equal("none", Render("{% for x in xs %}{{ x }}{% else %}none{% endfor %}", ("xs", new List<object?>())));
    }

    [Fact]
    public void SlicesIndexingAndMembership()
    {
        var xs = new List<object?> { 1L, 2L, 3L, 4L, 5L };
        Assert.Equal("5,4,3,2,1", Render("{{ xs[::-1] | join(',') }}", ("xs", xs)));
        Assert.Equal("2,3", Render("{{ xs[1:3] | join(',') }}", ("xs", xs)));
        Assert.Equal("4,5", Render("{{ xs[-2:] | join(',') }}", ("xs", xs)));
        Assert.Equal("1,3,5", Render("{{ xs[::2] | join(',') }}", ("xs", xs)));
        Assert.Equal("5", Render("{{ xs[-1] }}", ("xs", xs)));
        Assert.Equal("ello", Render("{{ s[1:] }}", ("s", "hello")));
        Assert.Equal("yes no", Render("{{ 'yes' if 3 in xs else 'no' }} {{ 'yes' if 9 in xs else 'no' }}", ("xs", xs)));
        Assert.Equal("yes", Render("{{ 'yes' if 'ell' in s else 'no' }}", ("s", "hello")));
        Assert.Equal("yes", Render("{{ 'yes' if 9 not in xs else 'no' }}", ("xs", xs)));
    }

    [Fact]
    public void StringMethods()
    {
        Assert.Equal("[x]", Render("[{{ ' x '.strip() }}]"));
        Assert.Equal("a|b|c", Render("{{ 'a,b,c'.split(',') | join('|') }}"));
        Assert.Equal("c", Render("{{ 'a,b,c'.split(',')[-1] }}"));
        Assert.Equal("True False", Render("{{ 'hello'.startswith('he') }} {{ 'hello'.endswith('x') }}"));
        Assert.Equal("heLLo", Render("{{ 'hello'.replace('l', 'L') }}"));
        Assert.Equal("Hello", Render("{{ 'hello' | capitalize }}"));
        Assert.Equal("x", Render("{{ '\n\nx\n'.strip('\n') }}"));
    }

    [Fact]
    public void DictsAndFilters()
    {
        var d = new Dictionary<string, object?> { ["b"] = 2L, ["a"] = 1L };
        Assert.Equal("b=2;a=1;", Render("{% for k, v in d.items() %}{{ k }}={{ v }};{% endfor %}", ("d", d)));
        Assert.Equal("a=1;b=2;", Render("{% for k, v in d | dictsort %}{{ k }}={{ v }};{% endfor %}", ("d", d)));
        Assert.Equal("1", Render("{{ d.get('a') }}", ("d", d)));
        Assert.Equal("fallback", Render("{{ d.get('zzz', 'fallback') }}", ("d", d)));
        Assert.Equal("3", Render("{{ [1, 2, 3] | length }}"));
        Assert.Equal("x", Render("{{ missing | default('x') }}"));
        Assert.Equal("1", Render("{{ 1 | default('x') }}"));
        Assert.Equal("2", Render("{{ xs | selectattr('ok') | list | length }}", ("xs", new List<object?>
        {
            new Dictionary<string, object?> { ["ok"] = true }, new Dictionary<string, object?> { ["ok"] = false }, new Dictionary<string, object?> { ["ok"] = true },
        })));
    }

    [Fact]
    public void ToJsonUsesPythonSeparatorsAndIndent()
    {
        var tool = new Dictionary<string, object?> { ["name"] = "f", ["args"] = new List<object?> { 1L, "é\"" }, ["ok"] = true, ["n"] = null };
        Assert.Equal("{\"name\": \"f\", \"args\": [1, \"é\\\"\"], \"ok\": true, \"n\": null}", Render("{{ t | tojson }}", ("t", tool)));
        Assert.Equal("{\n  \"a\": [\n    1\n  ]\n}", Render("{{ t | tojson(indent=2) }}", ("t", new Dictionary<string, object?> { ["a"] = new List<object?> { 1L } })));
    }

    [Fact]
    public void TestsAndUndefined()
    {
        Assert.Equal("False True", Render("{{ x is defined }} {{ x is not defined }}"));
        Assert.Equal("True", Render("{{ m.zzz is not defined }}", ("m", new Dictionary<string, object?>())));
        Assert.Equal("none", Render("{% if v is none %}none{% endif %}", ("v", null)));
        Assert.Equal("str", Render("{% if v is string %}str{% endif %}", ("v", "s")));
        Assert.Equal("", Render("{{ undefined_thing }}"));
        Assert.Equal("odd", Render("{{ 'odd' if 3 is odd else 'even' }}"));
    }

    [Fact]
    public void MacrosSetBlocksAndRange()
    {
        Assert.Equal("<b>hi</b>", Render("{% macro bold(t) %}<b>{{ t }}</b>{% endmacro %}{{ bold('hi') }}"));
        Assert.Equal("hi!", Render("{% macro shout(t, mark='!') %}{{ t }}{{ mark }}{% endmacro %}{{ shout('hi') }}"));
        Assert.Equal("[a b]", Render("{% set x %}a b{% endset %}[{{ x }}]"));
        Assert.Equal("0,1,2", Render("{{ range(3) | join(',') }}"));
        Assert.Equal("{{ not rendered }}", Render("{% raw %}{{ not rendered }}{% endraw %}"));
    }

    [Fact]
    public void ArithmeticAndComparison()
    {
        Assert.Equal("7 1 2 True", Render("{{ 3 + 4 }} {{ 7 % 3 }} {{ 7 // 3 }} {{ 1 < 2 and 2 < 3 }}"));
        Assert.Equal("1.5", Render("{{ 3 / 2 }}"));
        Assert.Equal("ab", Render("{{ 'a' ~ 'b' }}"));
        Assert.Equal("[1, 'a']", Render("{{ [1, 'a'] }}"));
    }

    [Fact]
    public void StrftimeNowUsesPythonDirectives()
    {
        string r = Render("{{ strftime_now('%Y-%m-%d') }}");
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", r);
        Assert.Equal(DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture), Render("{{ strftime_now('%-d %B %Y') }}"));
    }

    [Fact]
    public void UnsupportedSyntaxFailsWithAReadableMessage()
    {
        Assert.Contains("Unsupported tag", Assert.Throws<JinjaException>(() => new JinjaTemplate("{% include 'x' %}")).Message);
        Assert.Contains("Missing", Assert.Throws<JinjaException>(() => new JinjaTemplate("{% if x %}no end")).Message);
        Assert.Contains("Unclosed", Assert.Throws<JinjaException>(() => new JinjaTemplate("{{ x ")).Message);
        Assert.Contains("Unknown filter", Assert.Throws<JinjaException>(() => Render("{{ 1 | nosuchfilter }}")).Message);
    }

    [Fact]
    public void TemplatesCannotReachOutsideTheirData()
    {
        // there is no attribute access to .NET objects: only the provided dictionaries and lists
        Assert.Equal("", Render("{{ ''.__class__ }}"));
        Assert.Throws<JinjaException>(() => Render("{{ ''.__class__.__mro__() }}"));
    }
}
