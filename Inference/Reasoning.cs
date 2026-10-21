using LLMHostProject.Models;

namespace LLMHostProject.Inference;

/// <summary>How a model's chat template handles "thinking".</summary>
public enum ReasoningStyle
{
    /// <summary>The model has no reasoning mode we know about.</summary>
    None,
    /// <summary>The template supports switching thinking off with an empty think block (Qwen3: enable_thinking).</summary>
    ThinkSwitch,
    /// <summary>The generation prompt opens a think block itself (DeepSeek-R1 distills, QwQ).</summary>
    ThinkOpen,
    /// <summary>Harmony format (gpt-oss): a "Reasoning: low|medium|high" line in the system message.</summary>
    Harmony,
}

public static class Reasoning
{
    public static ReasoningStyle Detect(string? template)
    {
        if (string.IsNullOrEmpty(template)) return ReasoningStyle.None;
        if (template.Contains("<|channel|>") || template.Contains("reasoning_effort")) return ReasoningStyle.Harmony;
        if (template.Contains("enable_thinking")) return ReasoningStyle.ThinkSwitch;
        if (template.Contains("<think>")) return ReasoningStyle.ThinkOpen;
        return ReasoningStyle.None;
    }

    /// <summary>Normalises user input: off / low / medium / high / on, or null for "model default".</summary>
    public static string? NormalizeEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort)) return null;
        return effort.Trim().ToLowerInvariant() switch
        {
            "off" or "none" or "false" or "no" or "disabled" or "0" => "off",
            "minimal" or "low" => "low",
            "medium" or "med" => "medium",
            "high" or "max" => "high",
            "on" or "true" or "yes" or "enabled" or "auto" or "default" => "on",
            _ => null,
        };
    }

    /// <summary>
    /// How many reasoning tokens the model may spend before the think block is closed for it
    /// (-1 = unlimited). Only low and medium cap the budget.
    /// </summary>
    public static int Budget(string? effort) => NormalizeEffort(effort) switch { "low" => 512, "medium" => 2048, _ => -1 };

    /// <summary>System-message text for styles that carry the effort there (Harmony).</summary>
    public static string? SystemHint(ReasoningStyle style, string? effort)
    {
        if (style != ReasoningStyle.Harmony) return null;
        string e = NormalizeEffort(effort) switch { "off" or "low" => "low", "high" => "high", _ => "medium" };
        return "Reasoning: " + e;
    }

    /// <summary>
    /// Adjusts the already rendered prompt so the model does (not) think. Returns the prompt unchanged when the
    /// effort is "default" or the model has no thinking switch.
    /// </summary>
    public static string ApplyToPrompt(string prompt, ReasoningStyle style, string? effort)
    {
        if (NormalizeEffort(effort) != "off") return prompt;
        switch (style)
        {
            case ReasoningStyle.ThinkSwitch:
                return prompt.EndsWith("<think>\n", StringComparison.Ordinal) ? prompt + "\n</think>\n\n" : prompt + "<think>\n\n</think>\n\n";
            case ReasoningStyle.ThinkOpen:
                return prompt.EndsWith("<think>\n", StringComparison.Ordinal) || prompt.EndsWith("<think>", StringComparison.Ordinal)
                    ? prompt + "\n</think>\n\n" : prompt + "<think>\n\n</think>\n\n";
            default:
                return prompt;
        }
    }

    /// <summary>True when the rendered prompt ends inside an unclosed think block.</summary>
    public static bool EndsInsideThink(string prompt)
    {
        int open = prompt.LastIndexOf(TextScanner.ThinkOpen, StringComparison.Ordinal);
        if (open < 0) return false;
        int close = prompt.LastIndexOf(TextScanner.ThinkClose, StringComparison.Ordinal);
        return open > close;
    }

    /// <summary>Removes think blocks from assistant history so old reasoning does not eat the context window.</summary>
    public static string StripThink(string content)
    {
        int open = content.IndexOf(TextScanner.ThinkOpen, StringComparison.Ordinal);
        while (open >= 0)
        {
            int close = content.IndexOf(TextScanner.ThinkClose, open, StringComparison.Ordinal);
            if (close < 0) { content = content[..open]; break; }
            content = content.Remove(open, close + TextScanner.ThinkClose.Length - open).TrimStart('\n', '\r');
            open = content.IndexOf(TextScanner.ThinkOpen, StringComparison.Ordinal);
        }
        return content;
    }
}
