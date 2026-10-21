using System.Text;
using LLMHostProject.Core;
using LLMHostProject.Models;

namespace LLMHostProject.Inference;

/// <summary>Text put around one kind of message.</summary>
public class TurnFormat
{
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
}

/// <summary>A chat format written as plain prefixes and suffixes, for models whose template is missing or wrong.</summary>
public class ManualTemplate
{
    /// <summary>Text at the very start of the prompt (use only if the tokenizer does not add the BOS token itself).</summary>
    public string Bos { get; set; } = "";
    public TurnFormat System { get; set; } = new();
    public TurnFormat User { get; set; } = new();
    public TurnFormat Assistant { get; set; } = new();
    public TurnFormat Tool { get; set; } = new();
    /// <summary>Text that opens the assistant's answer at the end of the prompt (normally the assistant prefix; kept separate for formats that differ).</summary>
    public string GenerationPrefix { get; set; } = "";
    /// <summary>Generation stops when one of these appears (usually the assistant suffix, e.g. <c>&lt;|im_end|&gt;</c>).</summary>
    public List<string> StopStrings { get; set; } = new();

    public ManualTemplate Clone() => new()
    {
        Bos = Bos, GenerationPrefix = GenerationPrefix, StopStrings = new List<string>(StopStrings),
        System = new TurnFormat { Prefix = System.Prefix, Suffix = System.Suffix },
        User = new TurnFormat { Prefix = User.Prefix, Suffix = User.Suffix },
        Assistant = new TurnFormat { Prefix = Assistant.Prefix, Suffix = Assistant.Suffix },
        Tool = new TurnFormat { Prefix = Tool.Prefix, Suffix = Tool.Suffix },
    };
}

/// <summary>Per-model prompt-template choice (<see cref="LoadOptions.PromptTemplate"/>).</summary>
public class PromptTemplateSettings
{
    public const string Auto = "auto", Jinja = "jinja", Manual = "manual";

    /// <summary>"auto" = the template stored in the model file, "jinja" = <see cref="JinjaText"/>, "manual" = <see cref="ManualFormat"/>.</summary>
    public string Mode { get; set; } = Auto;
    /// <summary>Jinja chat template (the same language as the <c>chat_template</c> of Hugging Face models).</summary>
    public string? JinjaText { get; set; }
    public ManualTemplate ManualFormat { get; set; } = new();

    public bool IsOverride => Mode is Jinja or Manual;

    public PromptTemplateSettings Clone() => new() { Mode = Mode, JinjaText = JinjaText, ManualFormat = ManualFormat?.Clone() ?? new() };
}

/// <summary>Renders chat prompts from a custom template and previews templates without loading a model.</summary>
public static class PromptTemplates
{
    /// <summary>Builds the prompt from prefixes and suffixes. Unknown roles are treated as user turns.</summary>
    public static string RenderManual(ManualTemplate t, IReadOnlyList<(string Role, string Content)> messages, bool addAssistant)
    {
        var sb = new StringBuilder(t.Bos);
        foreach (var (role, content) in messages)
        {
            var f = role switch { "system" or "developer" => t.System, "assistant" => t.Assistant, "tool" or "function" => t.Tool, _ => t.User };
            sb.Append(f.Prefix).Append(content).Append(f.Suffix);
        }
        if (addAssistant) sb.Append(string.IsNullOrEmpty(t.GenerationPrefix) ? t.Assistant.Prefix : t.GenerationPrefix);
        return sb.ToString();
    }

    /// <summary>Message content as a list of parts, the form multimodal templates iterate over.</summary>
    private static List<object?> TypedContent(string text) =>
        new() { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } };

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JinjaTemplate, object> TypedCache = new();

    /// <summary>
    /// Some templates (SmolVLM / Idefics, Llama 3.2 Vision, Gemma 3) only work when a message's content is a list of
    /// <c>{type, text}</c> parts. Like llama.cpp, find out by rendering a probe: if the plain-string form does not print the text
    /// but the list form does, the template needs typed content.
    /// </summary>
    public static bool RequiresTypedContent(JinjaTemplate template)
    {
        if (TypedCache.TryGetValue(template, out var cached)) return (bool)cached;
        const string probe = "PROBE_5f3a";
        bool Prints(bool typed)
        {
            try
            {
                var vars = new Dictionary<string, object?>
                {
                    ["messages"] = new List<object?> { new Dictionary<string, object?> { ["role"] = "user", ["content"] = typed ? TypedContent(probe) : probe } },
                    ["add_generation_prompt"] = true, ["bos_token"] = "", ["eos_token"] = "",
                };
                return template.Render(vars).Contains(probe);
            }
            catch (JinjaException) { return false; }
        }
        bool needs = !Prints(false) && Prints(true);
        TypedCache.AddOrUpdate(template, needs);
        return needs;
    }

    /// <summary>Renders a Jinja chat template the way Hugging Face does (<c>messages</c>, <c>add_generation_prompt</c>, <c>bos_token</c>, <c>eos_token</c>).</summary>
    public static string RenderJinja(JinjaTemplate template, IReadOnlyList<(string Role, string Content)> messages, bool addAssistant,
        string bos, string eos, IDictionary<string, object?>? extra = null)
    {
        bool typed = RequiresTypedContent(template);
        var vars = new Dictionary<string, object?>
        {
            ["messages"] = messages.Select(m => (object?)new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = typed ? TypedContent(m.Content) : m.Content }).ToList(),
            ["add_generation_prompt"] = addAssistant,
            ["bos_token"] = bos,
            ["eos_token"] = eos,
        };
        if (extra != null) foreach (var kv in extra) vars[kv.Key] = kv.Value;
        return template.Render(vars);
    }

    /// <summary>Problems with a template choice, for validation messages (empty = fine).</summary>
    public static IEnumerable<string> Check(PromptTemplateSettings t)
    {
        if (t.Mode is not (PromptTemplateSettings.Auto or PromptTemplateSettings.Jinja or PromptTemplateSettings.Manual))
        { yield return "Mode must be auto, jinja or manual."; yield break; }
        if (t.Mode == PromptTemplateSettings.Jinja)
        {
            if (string.IsNullOrWhiteSpace(t.JinjaText)) { yield return "The Jinja template is empty."; yield break; }
            string? error = null;
            try { _ = new JinjaTemplate(t.JinjaText); } catch (JinjaException ex) { error = ex.Message; }
            if (error != null) yield return "The Jinja template cannot be parsed: " + error;
        }
        if (t.Mode == PromptTemplateSettings.Manual)
        {
            var m = t.ManualFormat;
            if (m == null) { yield return "The manual format is missing."; yield break; }
            if (string.IsNullOrEmpty(m.User.Prefix) && string.IsNullOrEmpty(m.User.Suffix) && string.IsNullOrEmpty(m.Assistant.Prefix))
                yield return "The manual format has no user or assistant markers.";
        }
    }

    /// <summary>The conversation used to test a template when a model is loaded and in the preview.</summary>
    public static readonly (string Role, string Content)[] SampleConversation =
    {
        ("system", "You are helpful."), ("user", "Hello"), ("assistant", "Hi there!"), ("user", "How are you?"),
    };

    /// <summary>What the prompt looks like for a model and template choice, without loading the model.</summary>
    public static (string Prompt, string Engine) Preview(LocalModel model, PromptTemplateSettings choice,
        IReadOnlyList<(string Role, string Content)> messages, bool addAssistant)
    {
        var info = FitEstimator.ReadModel(model.Path).Info;
        var (bos, eos) = SpecialTokens(model.Path, info);
        switch (choice.Mode)
        {
            case PromptTemplateSettings.Manual:
                return (RenderManual(choice.ManualFormat, messages, addAssistant), "manual");
            case PromptTemplateSettings.Jinja:
                return (RenderJinja(new JinjaTemplate(choice.JinjaText ?? ""), messages, addAssistant, bos, eos), "jinja");
            default:
                if (string.IsNullOrWhiteSpace(info.ChatTemplate)) throw new InvalidOperationException("The model file has no chat template; choose a Jinja or manual template.");
                return (RenderJinja(new JinjaTemplate(info.ChatTemplate), messages, addAssistant, bos, eos), "jinja (model template, built-in renderer)");
        }
    }

    /// <summary>Text of the model's BOS and EOS tokens (empty when the file does not say).</summary>
    public static (string Bos, string Eos) SpecialTokens(string path, GgufInfo info)
    {
        var ids = new List<long>();
        if (info.BosId >= 0) ids.Add(info.BosId);
        if (info.EosId >= 0) ids.Add(info.EosId);
        if (ids.Count == 0) return ("", "");
        var text = GgufInfo.ReadTokenText(path, info, ids);
        return (info.BosId >= 0 && text.TryGetValue(info.BosId, out var b) ? b : "", info.EosId >= 0 && text.TryGetValue(info.EosId, out var e) ? e : "");
    }
}
