using LLMHostProject.Inference;

namespace LLMHostProject.Models;

/// <summary>
/// What a model can do, derived from its GGUF metadata, chat template and multimodal projector. Shown as badges in the UI
/// and reported by the API (<c>/v1/models</c>, <c>/api/status</c>, <c>/api/models/local</c>).
/// </summary>
public record ModelCapabilities(bool Vision, bool Audio, bool Tools, bool Reasoning, bool Embedding, bool Moe, bool Rerank)
{
    public static readonly ModelCapabilities None = new(false, false, false, false, false, false, false);

    /// <param name="model">GGUF header of the model (null when unreadable).</param>
    /// <param name="mmproj">GGUF header of its multimodal projector, when there is one.</param>
    public static ModelCapabilities From(GgufInfo? model, GgufInfo? mmproj, string? fileName = null)
    {
        if (model == null) return None;
        bool rerank = model.PoolingType == 4 ||
                      (model.IsEmbeddingModel && ((fileName ?? model.Name).Contains("rerank", StringComparison.OrdinalIgnoreCase) ||
                                                  model.Name.Contains("rerank", StringComparison.OrdinalIgnoreCase)));
        bool embedding = model.IsEmbeddingModel && !rerank;
        bool chat = !model.IsEmbeddingModel;
        string? template = model.ChatTemplate;
        bool vision = chat && mmproj != null && mmproj.HasVisionEncoder;
        bool audio = chat && mmproj != null && mmproj.HasAudioEncoder;
        return new ModelCapabilities(
            Vision: vision,
            Audio: audio,
            Tools: chat && (ToolFormats.Detect(template) != ToolFormat.Generic || (template?.Contains("tools") ?? false)),
            Reasoning: chat && Inference.Reasoning.Detect(template) != ReasoningStyle.None,
            Embedding: embedding,
            Moe: model.IsMoe,
            Rerank: rerank);
    }

    /// <summary>Names of the capabilities that are set, in a stable order (for badges and CLI tables).</summary>
    public IReadOnlyList<string> Names()
    {
        var l = new List<string>();
        if (Vision) l.Add("vision");
        if (Audio) l.Add("audio");
        if (Tools) l.Add("tools");
        if (Reasoning) l.Add("reasoning");
        if (Embedding) l.Add("embedding");
        if (Rerank) l.Add("rerank");
        if (Moe) l.Add("moe");
        return l;
    }

    /// <summary>JSON shape used by the API: one boolean per capability plus the list of names.</summary>
    public object ToApi() => new { vision = Vision, audio = Audio, tools = Tools, reasoning = Reasoning, embedding = Embedding, rerank = Rerank, moe = Moe, names = Names() };
}
