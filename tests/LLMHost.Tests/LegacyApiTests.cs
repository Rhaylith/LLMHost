using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Api")]
public class LegacyModelsApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public LegacyModelsApiTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task EveryModelEntryHasTheV0Fields()
    {
        GgufBuilder.WriteFile("library/v0pub/v0model/v0-Q4_0.gguf", GgufBuilder.Build(experts: 4));
        var list = JsonNode.Parse(await _f.Http.GetStringAsync("/api/v0/models"))!;
        Assert.Equal("list", (string)list["object"]!);
        var e = list["data"]!.AsArray().First(x => (string)x!["id"]! == "v0-Q4_0")!;
        foreach (var key in new[] { "id", "object", "type", "publisher", "arch", "compatibility_type", "quantization", "state", "max_context_length", "capabilities" })
            Assert.True(e.AsObject().ContainsKey(key), key);
        Assert.Equal("llm", (string)e["type"]!);
        Assert.Equal("not-loaded", (string)e["state"]!);
        Assert.Equal("gguf", (string)e["compatibility_type"]!);
        Assert.Equal("v0pub", (string)e["publisher"]!);
        Assert.Equal(2048, (long)e["max_context_length"]!);
        Assert.Null(e["loaded_context_length"]);
    }

    [Fact]
    public async Task OneModelCanBeFetchedAndAnUnknownOneIs404()
    {
        GgufBuilder.WriteFile("library/v0pub/v0one/v0one-Q4_0.gguf", GgufBuilder.Build());
        var one = JsonNode.Parse(await _f.Http.GetStringAsync("/api/v0/models/v0one-Q4_0"))!;
        Assert.Equal("v0one-Q4_0", (string)one["id"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync("/api/v0/models/nothing-like-it")).StatusCode);
    }

    [Fact]
    public async Task EmbeddingModelsHaveTheEmbeddingsType()
    {
        GgufBuilder.WriteFile("library/v0pub/v0emb/v0emb-Q4_0.gguf", GgufBuilder.Build(poolingType: 1));
        var e = JsonNode.Parse(await _f.Http.GetStringAsync("/api/v0/models/v0emb-Q4_0"))!;
        Assert.Equal("embeddings", (string)e["type"]!);
    }

    [Theory]
    [InlineData("/api/v0/chat/completions", "chat")]
    [InlineData("/api/v0/completions", "chat")]
    [InlineData("/api/v0/embeddings", "embeddings")]
    [InlineData("/api/v0/models", "models:read")]
    public void TheRoutesNeedTheSamePermissionsAsTheV1Ones(string path, string permission)
    {
        string? p = Scopes.Required(path == "/api/v0/models" ? "GET" : "POST", path);
        Assert.Equal(permission, p);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class LegacyChatApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public LegacyChatApiTests(ServerFixture f) => _f = f;

    private async Task<string> ModelId()
    {
        var path = Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!;
        var rt = _f.Host.Models.Loaded.FirstOrDefault(r => r.Identifier == "v0-model")
                 ?? await _f.Host.Models.LoadAsync(path, new LoadOptions { Device = "cpu", ContextSize = 1024, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Vision = false, Identifier = "v0-model" });
        return rt.Identifier;
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [VlmFact]
    public async Task ChatCompletionsAddsTheV0Extras()
    {
        string m = await ModelId();
        var r = await _f.Http.PostAsync("/api/v0/chat/completions", Body($$"""{"model":"{{m}}","max_tokens":4,"temperature":0,"messages":[{"role":"user","content":"Hi"}]}"""));
        Assert.True(r.IsSuccessStatusCode);
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        Assert.NotNull(j["choices"]![0]!["message"]!["content"]);
        foreach (var key in new[] { "tokens_per_second", "time_to_first_token", "generation_time", "stop_reason" }) Assert.NotNull(j["stats"]![key]);
        Assert.Equal("loaded", (string)j["model_info"]!["state"]!);
        Assert.Equal(1024, (long)j["model_info"]!["loaded_context_length"]!);
        Assert.Equal("gguf", (string)j["runtime"]!["supported_formats"]![0]!);
        Assert.Equal((string)j["choices"]![0]!["finish_reason"]!, (string)j["stats"]!["stop_reason"]!);
        // the v1 route is unchanged
        var v1 = JsonNode.Parse(await (await _f.Http.PostAsync("/v1/chat/completions", Body($$"""{"model":"{{m}}","max_tokens":2,"messages":[{"role":"user","content":"Hi"}]}"""))).Content.ReadAsStringAsync())!;
        Assert.Null(v1["model_info"]);
    }

    [VlmFact]
    public async Task StreamingPassesThroughUnchanged()
    {
        string m = await ModelId();
        var r = await _f.Http.PostAsync("/api/v0/chat/completions", Body($$"""{"model":"{{m}}","max_tokens":4,"stream":true,"messages":[{"role":"user","content":"Hi"}]}"""));
        Assert.Equal("text/event-stream", r.Content.Headers.ContentType?.MediaType);
        string text = await r.Content.ReadAsStringAsync();
        Assert.Contains("data: ", text);
        Assert.Contains("[DONE]", text);
    }

    [VlmFact]
    public async Task CompletionsWork()
    {
        string m = await ModelId();
        var r = await _f.Http.PostAsync("/api/v0/completions", Body($$"""{"model":"{{m}}","prompt":"Hello","max_tokens":3}"""));
        Assert.True(r.IsSuccessStatusCode);
        Assert.NotNull(JsonNode.Parse(await r.Content.ReadAsStringAsync())!["choices"]![0]!["text"]);
    }

    [VlmFact]
    public async Task ErrorsKeepTheirStatus()
    {
        _f.Host.Models.UnloadAll();
        AppSettings.Current.AutoLoadOnRequest = false;
        try
        {
            var r = await _f.Http.PostAsync("/api/v0/chat/completions", Body("""{"model":"not-loaded-anywhere","messages":[{"role":"user","content":"Hi"}]}"""));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        finally { AppSettings.Current.AutoLoadOnRequest = true; }
    }
}
