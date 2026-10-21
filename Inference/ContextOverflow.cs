namespace LLMHostProject.Inference;

/// <summary>What to do when a conversation is longer than the context window.</summary>
public enum OverflowPolicy
{
    /// <summary>Leave out the oldest turns (never the system prompt or the newest message). The default.</summary>
    DropOldest,
    /// <summary>Keep the system prompt, the first question and the newest turns; leave out the middle and say so.</summary>
    TruncateMiddle,
    /// <summary>Like DropOldest for the prompt; while answering, the oldest part of the cache is discarded so the answer can go on past the window.</summary>
    RollingWindow,
    /// <summary>Never change the conversation: the answer simply stops (finish reason "length") when the window is full.</summary>
    StopAtLimit,
    /// <summary>Refuse a request that does not fit (HTTP 400, code <c>context_length_exceeded</c>).</summary>
    Error,
}

/// <summary>The prompt does not fit and the policy says not to cut anything (<see cref="OverflowPolicy.Error"/>).</summary>
public class ContextLengthException : InvalidOperationException
{
    public ContextLengthException(string message) : base(message) { }
}

public static class ContextOverflow
{
    public static readonly string[] Names = { "dropOldest", "truncateMiddle", "rollingWindow", "stopAtLimit", "error" };

    /// <summary>Accepts camelCase, snake_case and kebab-case spellings; null / empty = the default.</summary>
    public static bool TryParse(string? s, out OverflowPolicy policy)
    {
        policy = OverflowPolicy.DropOldest;
        if (string.IsNullOrWhiteSpace(s)) return true;
        switch (s.Trim().Replace("_", "").Replace("-", "").ToLowerInvariant())
        {
            case "dropoldest": case "drop": case "default": policy = OverflowPolicy.DropOldest; return true;
            case "truncatemiddle": case "middle": policy = OverflowPolicy.TruncateMiddle; return true;
            case "rollingwindow": case "rolling": policy = OverflowPolicy.RollingWindow; return true;
            case "stopatlimit": case "stop": policy = OverflowPolicy.StopAtLimit; return true;
            case "error": case "fail": policy = OverflowPolicy.Error; return true;
        }
        return false;
    }

    public static OverflowPolicy Parse(string? s) => TryParse(s, out var p) ? p : OverflowPolicy.DropOldest;
}
