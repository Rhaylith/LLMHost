namespace LLMHostProject.Models;

public record CatalogEntry(string Key, string Family, string Name, string Params, string Repo,
    string RecommendedQuant, string[] Tags, string Notes = "")
{
    /// <summary>True for models that accept images (multi-modal); everything else is a text LLM.</summary>
    public bool IsVision => Tags.Contains("vision");
}

/// <summary>Curated list of major open-weight model families published as GGUF on Hugging Face.</summary>
public static class Catalog
{
    public static readonly IReadOnlyList<CatalogEntry> Entries = new CatalogEntry[]
    {
        new("llama3.2-1b", "Llama", "Llama 3.2 1B Instruct", "1B", "bartowski/Llama-3.2-1B-Instruct-GGUF", "Q8_0", new[]{"chat","tiny"}),
        new("llama3.2-3b", "Llama", "Llama 3.2 3B Instruct", "3B", "bartowski/Llama-3.2-3B-Instruct-GGUF", "Q4_K_M", new[]{"chat","small"}),
        new("llama3.1-8b", "Llama", "Llama 3.1 8B Instruct", "8B", "bartowski/Meta-Llama-3.1-8B-Instruct-GGUF", "Q4_K_M", new[]{"chat","tools"}),
        new("llama3.3-70b", "Llama", "Llama 3.3 70B Instruct", "70B", "bartowski/Llama-3.3-70B-Instruct-GGUF", "Q4_K_M", new[]{"chat","large"}),

        new("qwen2.5-0.5b", "Qwen", "Qwen2.5 0.5B Instruct", "0.5B", "Qwen/Qwen2.5-0.5B-Instruct-GGUF", "q4_k_m", new[]{"chat","tiny"}, "Good for a quick smoke test"),
        new("qwen2.5-7b", "Qwen", "Qwen2.5 7B Instruct", "7B", "bartowski/Qwen2.5-7B-Instruct-GGUF", "Q4_K_M", new[]{"chat","tools"}),
        new("qwen2.5-coder-7b", "Qwen", "Qwen2.5 Coder 7B Instruct", "7B", "bartowski/Qwen2.5-Coder-7B-Instruct-GGUF", "Q4_K_M", new[]{"code"}),
        new("qwen3-8b", "Qwen", "Qwen3 8B", "8B", "unsloth/Qwen3-8B-GGUF", "Q4_K_M", new[]{"chat","reasoning","tools"}),
        new("qwen3-14b", "Qwen", "Qwen3 14B", "14B", "unsloth/Qwen3-14B-GGUF", "Q4_K_M", new[]{"chat","reasoning","tools"}),
        new("qwen3-30b-a3b", "Qwen", "Qwen3 30B-A3B (MoE)", "30B", "unsloth/Qwen3-30B-A3B-GGUF", "Q4_K_M", new[]{"chat","reasoning","moe"}),

        new("mistral-7b", "Mistral", "Mistral 7B Instruct v0.3", "7B", "bartowski/Mistral-7B-Instruct-v0.3-GGUF", "Q4_K_M", new[]{"chat"}),
        new("mistral-nemo-12b", "Mistral", "Mistral Nemo Instruct 12B", "12B", "bartowski/Mistral-Nemo-Instruct-2407-GGUF", "Q4_K_M", new[]{"chat"}),
        new("mistral-small-24b", "Mistral", "Mistral Small 24B Instruct", "24B", "bartowski/Mistral-Small-24B-Instruct-2501-GGUF", "Q4_K_M", new[]{"chat","tools"}),

        new("gemma2-9b", "Gemma", "Gemma 2 9B IT", "9B", "bartowski/gemma-2-9b-it-GGUF", "Q4_K_M", new[]{"chat"}),
        new("gemma3-4b", "Gemma", "Gemma 3 4B IT", "4B", "unsloth/gemma-3-4b-it-GGUF", "Q4_K_M", new[]{"chat","small","vision"}),
        new("gemma3-12b", "Gemma", "Gemma 3 12B IT", "12B", "unsloth/gemma-3-12b-it-GGUF", "Q4_K_M", new[]{"chat","vision"}),
        new("gemma3-27b", "Gemma", "Gemma 3 27B IT", "27B", "unsloth/gemma-3-27b-it-GGUF", "Q4_K_M", new[]{"chat","large","vision"}),

        new("smolvlm-256m", "SmolVLM", "SmolVLM 256M Instruct", "256M", "ggml-org/SmolVLM-256M-Instruct-GGUF", "Q8_0", new[]{"vision","tiny"}, "Smallest vision model; good for a smoke test (~270 MB with projector)"),
        new("smolvlm2-2.2b", "SmolVLM", "SmolVLM2 2.2B Instruct", "2.2B", "ggml-org/SmolVLM2-2.2B-Instruct-GGUF", "Q4_K_M", new[]{"vision","small"}),
        new("qwen2.5-vl-3b", "Qwen", "Qwen2.5-VL 3B Instruct", "3B", "ggml-org/Qwen2.5-VL-3B-Instruct-GGUF", "Q4_K_M", new[]{"vision","small"}),
        new("qwen2.5-vl-7b", "Qwen", "Qwen2.5-VL 7B Instruct", "7B", "ggml-org/Qwen2.5-VL-7B-Instruct-GGUF", "Q4_K_M", new[]{"vision"}),
        new("pixtral-12b", "Mistral", "Pixtral 12B", "12B", "ggml-org/pixtral-12b-GGUF", "Q4_K_M", new[]{"vision"}),

        new("phi-4", "Phi", "Phi-4 14B", "14B", "bartowski/phi-4-GGUF", "Q4_K_M", new[]{"chat","reasoning"}),
        new("phi-3.5-mini", "Phi", "Phi-3.5 Mini Instruct", "3.8B", "bartowski/Phi-3.5-mini-instruct-GGUF", "Q4_K_M", new[]{"chat","small"}),

        new("deepseek-r1-qwen-7b", "DeepSeek", "DeepSeek R1 Distill Qwen 7B", "7B", "unsloth/DeepSeek-R1-Distill-Qwen-7B-GGUF", "Q4_K_M", new[]{"reasoning"}),
        new("deepseek-r1-llama-8b", "DeepSeek", "DeepSeek R1 Distill Llama 8B", "8B", "unsloth/DeepSeek-R1-Distill-Llama-8B-GGUF", "Q4_K_M", new[]{"reasoning"}),
        new("deepseek-r1-qwen-14b", "DeepSeek", "DeepSeek R1 Distill Qwen 14B", "14B", "unsloth/DeepSeek-R1-Distill-Qwen-14B-GGUF", "Q4_K_M", new[]{"reasoning"}),

        new("gpt-oss-20b", "OpenAI", "gpt-oss 20B", "20B", "unsloth/gpt-oss-20b-GGUF", "Q4_K_M", new[]{"chat","reasoning","moe"}),

        new("granite-3.1-8b", "IBM Granite", "Granite 3.1 8B Instruct", "8B", "bartowski/granite-3.1-8b-instruct-GGUF", "Q4_K_M", new[]{"chat","tools"}),
        new("smollm2-1.7b", "SmolLM", "SmolLM2 1.7B Instruct", "1.7B", "bartowski/SmolLM2-1.7B-Instruct-GGUF", "Q8_0", new[]{"chat","tiny"}),

        new("nomic-embed-v1.5", "Embedding", "Nomic Embed Text v1.5", "137M", "nomic-ai/nomic-embed-text-v1.5-GGUF", "Q4_K_M", new[]{"embeddings"}),
    };

    public static CatalogEntry? Find(string keyOrRepo) =>
        Entries.FirstOrDefault(e => e.Key.Equals(keyOrRepo, StringComparison.OrdinalIgnoreCase)
                                    || e.Repo.Equals(keyOrRepo, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pick the file that best matches a quantization name (case-insensitive), else the closest to Q4_K_M.</summary>
    public static RemoteFile? PickFile(IReadOnlyList<RemoteFile> files, string? quant)
    {
        if (files.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(quant))
        {
            var exact = files.FirstOrDefault(f => f.Quant.Equals(quant, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        foreach (var q in new[] { "Q4_K_M", "Q4_K_S", "Q5_K_M", "Q4_0", "Q8_0" })
        {
            var f = files.FirstOrDefault(x => x.Quant == q);
            if (f != null) return f;
        }
        return files[0];
    }
}
