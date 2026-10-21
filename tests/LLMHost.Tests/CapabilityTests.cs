using LLMHostProject.Core;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class CapabilityTests
{
    private static GgufInfo Read(string file, byte[] bytes) => GgufInfo.Read(GgufBuilder.WriteFile("caps/" + file, bytes));

    [Fact]
    public void PlainChatModelHasNoSpecialCapabilities()
    {
        var m = Read("plain.gguf", GgufBuilder.Build(chatTemplate: "{{ messages }}"));
        var c = ModelCapabilities.From(m, null);
        Assert.Empty(c.Names());
    }

    [Fact]
    public void ToolsAndReasoningComeFromTheTemplate()
    {
        var m = Read("qwen.gguf", GgufBuilder.Build(chatTemplate: "{% if tools %}<tool_call>{% endif %}{% if enable_thinking %}<think>{% endif %}"));
        var c = ModelCapabilities.From(m, null);
        Assert.True(c.Tools);
        Assert.True(c.Reasoning);
        Assert.False(c.Vision);
    }

    [Fact]
    public void ToolsMentionedOnlyAsVariableStillCount()
    {
        var m = Read("tools-var.gguf", GgufBuilder.Build(chatTemplate: "{% for t in tools %}{{ t }}{% endfor %}"));
        Assert.True(ModelCapabilities.From(m, null).Tools);
    }

    [Fact]
    public void ProjectorDecidesVisionAndAudio()
    {
        var m = Read("vlm.gguf", GgufBuilder.Build());
        var vision = Read("mmproj-v.gguf", GgufBuilder.Build(arch: "clip", clipVision: true, clipAudio: false));
        var omni = Read("mmproj-o.gguf", GgufBuilder.Build(arch: "clip", clipVision: true, clipAudio: true));

        Assert.True(ModelCapabilities.From(m, vision).Vision);
        Assert.False(ModelCapabilities.From(m, vision).Audio);
        Assert.True(ModelCapabilities.From(m, omni).Audio);
        Assert.False(ModelCapabilities.From(m, null).Vision);
    }

    [Fact]
    public void EmbeddingAndRerankModelsAreTold_Apart()
    {
        var emb = Read("emb.gguf", GgufBuilder.Build(arch: "bert", poolingType: 1, name: "bge-small"));
        var rank = Read("rank.gguf", GgufBuilder.Build(arch: "bert", poolingType: 4, name: "bge-reranker-v2"));
        var byName = Read("rank2.gguf", GgufBuilder.Build(arch: "bert", poolingType: 2, name: "Qwen3-Reranker"));

        var e = ModelCapabilities.From(emb, null);
        Assert.True(e.Embedding); Assert.False(e.Rerank);
        var r = ModelCapabilities.From(rank, null);
        Assert.True(r.Rerank); Assert.False(r.Embedding);
        Assert.True(ModelCapabilities.From(byName, null).Rerank);
        // an embedding model is never offered chat features
        Assert.False(ModelCapabilities.From(emb, null).Tools);
    }

    [Fact]
    public void MoeFollowsExpertCount()
    {
        var moe = Read("moe.gguf", GgufBuilder.Build(experts: 8));
        Assert.True(ModelCapabilities.From(moe, null).Moe);
        Assert.False(ModelCapabilities.From(Read("dense.gguf", GgufBuilder.Build()), null).Moe);
    }

    [Fact]
    public void UnreadableModelHasNoCapabilities() => Assert.Equal(ModelCapabilities.None, ModelCapabilities.From(null, null));

    [Fact]
    public void LibraryAttachesCapabilitiesAndApiShape()
    {
        string dir = Path.Combine(AppSettings.Current.ResolvedModelsDirectory, "pub", "caps-repo");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "vision-model.gguf"), GgufBuilder.Build(chatTemplate: "<tool_call>"));
        File.WriteAllBytes(Path.Combine(dir, "mmproj-vision-model.gguf"), GgufBuilder.Build(arch: "clip", clipVision: true));

        var model = new ModelLibrary().List().Single(x => x.Id == "pub/caps-repo/vision-model.gguf");

        Assert.True(model.Capabilities.Vision);
        Assert.True(model.Capabilities.Tools);
        var json = System.Text.Json.JsonSerializer.Serialize(model.Capabilities.ToApi(), Json.Options);
        Assert.Contains("\"vision\": true", json);
        Assert.Contains("\"names\"", json);
    }
}

[Trait("Category", "Unit")]
public class DraftPairingTests
{
    private static LocalModel Make(string file, string arch, int vocab, long size, bool embedding = false)
    {
        string path = GgufBuilder.WriteFile("drafts/" + file, GgufBuilder.Build(arch: arch, vocab: vocab));
        return new LocalModel { Id = "pub/repo/" + file, Path = path, FileName = file, Architecture = arch, SizeBytes = size, IsEmbedding = embedding, Name = file };
    }

    [Fact]
    public void OnlySmallModelsOfTheSameFamilyAndVocabularyQualify()
    {
        var target = Make("big.gguf", "qwen3", 1000, 9_000_000_000);
        var good = Make("small.gguf", "qwen3", 1000, 600_000_000);
        var slightlyDifferentVocab = Make("pad.gguf", "qwen3", 1100, 600_000_000);      // within 128 tokens: padding differences
        var wrongVocab = Make("vocab.gguf", "qwen3", 5000, 600_000_000);
        var wrongArch = Make("arch.gguf", "llama", 1000, 600_000_000);
        var tooBig = Make("mid.gguf", "qwen3", 1000, 4_000_000_000);
        var embed = Make("emb.gguf", "qwen3", 1000, 100_000_000, embedding: true);

        var found = DraftPairing.LocalCandidates(target, new[] { target, good, slightlyDifferentVocab, wrongVocab, wrongArch, tooBig, embed })
            .Select(m => m.FileName).ToList();

        Assert.Equal(new[] { "pad.gguf", "small.gguf" }.OrderBy(x => x), found.OrderBy(x => x));       // the padded vocabulary is still compatible
    }

    [Fact]
    public void SuggestionsFollowTheArchitecture()
    {
        Assert.Contains(DraftPairing.Suggestions(new LocalModel { Architecture = "qwen3", Name = "Qwen3 14B" }), s => s.Repo.Contains("Qwen3-0.6B"));
        Assert.Contains(DraftPairing.Suggestions(new LocalModel { Architecture = "llama", Name = "Llama-3.1-8B-Instruct", Id = "x/Llama-3.1-8B" }), s => s.Repo.Contains("Llama-3.2-1B"));
        Assert.Empty(DraftPairing.Suggestions(new LocalModel { Architecture = "llama", Name = "Vicuna", Id = "x/vicuna" }));
        Assert.Empty(DraftPairing.Suggestions(new LocalModel { Architecture = "mamba", Name = "m" }));
    }

    [Fact]
    public void DraftOptionsAreValidated()
    {
        var s = new AppSettings();
        s.DefaultLoad.DraftTokens = 500; s.DefaultLoad.DraftMinP = 3;
        var f = SettingsValidator.Validate(s).Select(e => e.Field).ToList();
        Assert.Contains("defaultLoad.draftTokens", f);
        Assert.Contains("defaultLoad.draftMinP", f);
    }
}
