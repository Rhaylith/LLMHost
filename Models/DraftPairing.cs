using LLMHostProject.Inference;

namespace LLMHostProject.Models;

public record DraftSuggestion(string Repo, string Why);

/// <summary>Helps to pick a draft model for speculative decoding: it must share the main model's vocabulary and be much smaller.</summary>
public static class DraftPairing
{
    /// <summary>A draft larger than this fraction of the main model costs more than it saves.</summary>
    public const double MaxSizeFraction = 1.0 / 6;

    /// <summary>Downloaded models that can serve as draft for <paramref name="target"/>, biggest (usually most accurate) first.</summary>
    public static IReadOnlyList<LocalModel> LocalCandidates(LocalModel target, IEnumerable<LocalModel> library)
    {
        long targetVocab = VocabOf(target);
        return library
            .Where(m => m.Path != target.Path && !m.IsEmbedding && m.Architecture == target.Architecture)
            .Where(m => m.SizeBytes <= target.SizeBytes * MaxSizeFraction)
            .Where(m => targetVocab == 0 || Math.Abs(VocabOf(m) - targetVocab) <= 128)
            .OrderByDescending(m => m.SizeBytes)
            .ToList();
    }

    private static long VocabOf(LocalModel m)
    {
        try { return FitEstimator.ReadModel(m.Path).Info.VocabSize; } catch { return 0; }
    }

    /// <summary>Well-known small models of the same family to download (the vocabulary is checked again when loading).</summary>
    public static IReadOnlyList<DraftSuggestion> Suggestions(LocalModel target)
    {
        var list = new List<DraftSuggestion>();
        string n = (target.Name + " " + target.Id).ToLowerInvariant();
        switch (target.Architecture)
        {
            case "qwen3": case "qwen3moe":
                list.Add(new("unsloth/Qwen3-0.6B-GGUF", "Qwen3 0.6B shares the Qwen3 vocabulary")); break;
            case "qwen2":
                list.Add(new("Qwen/Qwen2.5-0.5B-Instruct-GGUF", "Qwen2.5 0.5B shares the Qwen2.5 vocabulary")); break;
            case "llama" when n.Contains("llama-3") || n.Contains("llama3") || n.Contains("llama 3"):
                list.Add(new("bartowski/Llama-3.2-1B-Instruct-GGUF", "Llama 3.2 1B shares the Llama 3 vocabulary")); break;
            case "gemma3":
                list.Add(new("unsloth/gemma-3-270m-it-GGUF", "Gemma 3 270M shares the Gemma 3 vocabulary")); break;
        }
        return list;
    }
}
