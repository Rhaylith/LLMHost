using System.Text.Json.Nodes;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class ModelCardTests
{
    [Theory]
    [InlineData("apache-2.0", "open")]
    [InlineData("MIT", "open")]
    [InlineData("cc-by-4.0", "open")]
    [InlineData("cc-by-nc-4.0", "noncommercial")]
    [InlineData("cc-by-nc-sa-4.0", "noncommercial")]
    [InlineData("mrl", "restricted")]
    [InlineData("llama3.1", "restricted")]
    [InlineData("gemma", "restricted")]
    [InlineData("other", "restricted")]
    [InlineData("research-only", "noncommercial")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void LicencesAreClassified(string? id, string kind) => Assert.Equal(kind, LicenseInfo.Classify(id).Kind);

    [Fact]
    public void OnlyRestrictedLicencesWarn()
    {
        Assert.Null(LicenseInfo.Classify("apache-2.0").Warning);
        Assert.Contains("Non-commercial", LicenseInfo.Classify("cc-by-nc-4.0").Warning);
        Assert.NotNull(LicenseInfo.Classify("llama3").Warning);
    }

    [Fact]
    public void RemoteCapabilitiesComeFromTagsAndNames()
    {
        var vlm = RemoteCapabilities.FromTags(new[] { "gguf", "image-text-to-text" }, "image-text-to-text", "ggml-org/SmolVLM-256M-Instruct-GGUF");
        Assert.True(vlm.Vision); Assert.False(vlm.Embedding);

        var q3 = RemoteCapabilities.FromTags(new[] { "gguf" }, "text-generation", "unsloth/Qwen3-30B-A3B-GGUF");
        Assert.True(q3.Reasoning); Assert.True(q3.Moe); Assert.False(q3.Vision);

        var emb = RemoteCapabilities.FromTags(new[] { "sentence-transformers" }, "feature-extraction", "ggml-org/bge-small-en-v1.5-Q8_0-GGUF");
        Assert.True(emb.Embedding); Assert.False(emb.Tools); Assert.False(emb.Reasoning);

        var rr = RemoteCapabilities.FromTags(null, null, "gpustack/bge-reranker-v2-m3-GGUF");
        Assert.True(rr.Rerank); Assert.False(rr.Embedding);

        var tools = RemoteCapabilities.FromTags(new[] { "function-calling" }, "text-generation", "x/y-GGUF");
        Assert.True(tools.Tools);
    }

    [Fact]
    public void FrontMatterIsRemoved()
    {
        Assert.Equal("# Title\nbody", ModelCardText.StripFrontMatter("---\nlicense: mit\ntags:\n- a\n---\n\n# Title\nbody"));
        Assert.Equal("# Title", ModelCardText.StripFrontMatter("﻿---\r\nlicense: mit\r\n---\r\n# Title"));
        Assert.Equal("no front matter\n---\nrule", ModelCardText.StripFrontMatter("no front matter\n---\nrule"));
        Assert.Equal("", ModelCardText.StripFrontMatter("---\nlicense: mit\n---"));
    }

    [Fact]
    public void TheGgufHeaderCarriesLicenceAndBaseModel()
    {
        string path = GgufBuilder.WriteFile("card/lic.gguf", GgufBuilder.Build(license: "apache-2.0", baseModel: "Qwen/Qwen3-8B"));
        var g = GgufInfo.Read(path);
        Assert.Equal("apache-2.0", g.License);
        Assert.Equal("Qwen/Qwen3-8B", g.BaseModel);
        Assert.Null(GgufInfo.Read(GgufBuilder.WriteFile("card/none.gguf", GgufBuilder.Build())).License);
    }

    [Theory]
    [InlineData("owner/name", true)]
    [InlineData("a.b/c-d_e", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("../x", false)]
    [InlineData("a/..", false)]
    [InlineData("owner/name/extra", false)]
    [InlineData("owner", false)]
    [InlineData("", false)]
    public void RepositoryIdsAreValidated(string repo, bool ok) => Assert.Equal(ok, HuggingFace.ValidRepo(repo));
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class ModelInfoApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ModelInfoApiTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task LocalModelInfoShowsTheArchitectureTable()
    {
        GgufBuilder.WriteFile("library/infopub/infomodel/info-Q4_0.gguf", GgufBuilder.Build(blocks: 6, license: "cc-by-nc-4.0", baseModel: "x/base", experts: 8));
        var id = _f.Host.Library.List().First(m => m.FileName == "info-Q4_0.gguf").Id;
        var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/models/info?id=" + Uri.EscapeDataString(id)))!;
        Assert.Equal("llama", (string)j["architecture"]!);
        Assert.Equal(6, (int)j["blockCount"]!);
        Assert.Equal(8, (int)j["expertCount"]!);
        Assert.Equal("noncommercial", (string)j["licenseKind"]!);
        Assert.Contains("Non-commercial", (string)j["licenseWarning"]!);
        Assert.Equal("x/base", (string)j["baseModel"]!);
        Assert.True((bool)j["capabilities"]!["moe"]!);
    }

    [Fact]
    public async Task ABadRepositoryIsRefusedWithoutAskingTheNetwork()
    {
        var r = await _f.Http.GetAsync("/api/models/card?repo=" + Uri.EscapeDataString("noslash"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
    }
}

[Trait("Category", "Unit")]
public class RemoteVisionNameTests
{
    [Theory]
    [InlineData("ggml-org/SmolVLM2-2.2B-Instruct-GGUF")]
    [InlineData("bartowski/Qwen2.5-VL-7B-Instruct-GGUF")]
    [InlineData("unsloth/Qwen3-VL-8B-Instruct-GGUF")]
    [InlineData("mradermacher/llava-v1.6-mistral-7b-GGUF")]
    [InlineData("lmstudio-community/pixtral-12b-GGUF")]
    [InlineData("x/MiniCPM-V-2_6-gguf")]
    [InlineData("x/Llama-3.2-11B-Vision-Instruct-GGUF")]
    public void VisionModelsWithoutAVisionTagAreRecognisedByName(string repo) =>
        Assert.True(RemoteCapabilities.FromTags(new[] { "gguf" }, null, repo).Vision);

    [Theory]
    [InlineData("unsloth/Llama-3.2-1B-Instruct-GGUF")]
    [InlineData("bartowski/vllm-compat-7B-GGUF")]
    [InlineData("x/Qwen3-30B-A3B-GGUF")]
    [InlineData("bartowski/Meta-Llama-3.1-8B-Instruct-GGUF")]
    public void OrdinaryTextModelsAreNotMistakenForVisionModels(string repo) =>
        Assert.False(RemoteCapabilities.FromTags(new[] { "gguf" }, "text-generation", repo).Vision);
}
