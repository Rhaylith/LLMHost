using LLMHostProject.Core;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

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
