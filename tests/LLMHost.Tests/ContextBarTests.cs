using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Api")]
public class MessageStatsApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public MessageStatsApiTests(ServerFixture f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task WithoutALoadedModelTheUsageSaysSo()
    {
        _f.Host.Models.UnloadAll();
        var res = await _f.Http.PostAsync("/api/context/usage", Body("""{"messages":[{"role":"user","content":"hi"}]}"""));
        Assert.True(res.IsSuccessStatusCode);
        Assert.False((bool)JsonNode.Parse(await res.Content.ReadAsStringAsync())!["loaded"]!);
    }

    [Fact]
    public async Task StatsStoredWithAnAssistantMessageComeBackUnchanged()
    {
        string id = "st" + Guid.NewGuid().ToString("N")[..8];
        const string chat = """
            {"title":"stats","messages":[{"id":"a","role":"user","content":"q"},
              {"id":"b","parentId":"a","role":"assistant","content":"x","stats":{"tokens":12,"tokensPerSecond":31.5,"ttftSeconds":0.42,"stopReason":"length","promptCachedTokens":7,"draftTokens":10,"draftAccepted":6,"loadSeconds":2.5}}]}
            """;
        Assert.True((await _f.Http.PutAsync("/api/chats/" + id, Body(chat))).IsSuccessStatusCode);
        var back = JsonNode.Parse(await _f.Http.GetStringAsync("/api/chats/" + id))!;
        var s = back["messages"]![1]!["stats"]!;
        Assert.Equal(12, (int)s["tokens"]!);
        Assert.Equal(31.5, (double)s["tokensPerSecond"]!);
        Assert.Equal("length", (string)s["stopReason"]!);
        Assert.Equal(7, (int)s["promptCachedTokens"]!);
        Assert.Equal(6, (int)s["draftAccepted"]!);
        Assert.Equal(2.5, (double)s["loadSeconds"]!);
        Assert.Null(back["messages"]![0]!["stats"]);
        await _f.Http.DeleteAsync("/api/chats/" + id);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class ContextUsageIntegrationTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ContextUsageIntegrationTests(ServerFixture f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<string> ModelId()
    {
        var path = Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!;
        var rt = _f.Host.Models.Loaded.FirstOrDefault(r => r.Identifier == "ctxbar-model")
                 ?? await _f.Host.Models.LoadAsync(path, new LoadOptions { Device = "cpu", ContextSize = 1024, MaxConcurrent = 1, CacheSlots = 1, Threads = 8, SkipFitCheck = true, Vision = false, Identifier = "ctxbar-model" });
        return rt.Identifier;
    }

    private async Task<JsonNode> Usage(string json)
    {
        var r = await _f.Http.PostAsync("/api/context/usage", Body(json));
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    }

    [VlmFact]
    public async Task TheCountGrowsWithEveryPartOfThePrompt()
    {
        string m = await ModelId();
        var empty = await Usage($$"""{"model":"{{m}}","messages":[]}""");
        Assert.True((bool)empty["loaded"]!);
        Assert.Equal(1024, (int)empty["context"]!);
        Assert.Equal(0, (int)empty["used"]!);

        var one = await Usage($$"""{"model":"{{m}}","messages":[{"role":"user","content":"Hello there, how are you today?"}]}""");
        Assert.True((int)one["prompt"]! > 5);
        var withSystem = await Usage($$"""{"model":"{{m}}","system":"You are a pirate who always answers in rhyme.","messages":[{"role":"user","content":"Hello there, how are you today?"}]}""");
        Assert.True((int)withSystem["prompt"]! > (int)one["prompt"]!);
        var typing = await Usage($$"""{"model":"{{m}}","system":"You are a pirate who always answers in rhyme.","input":"and a long draft of a question that is still being typed","messages":[{"role":"user","content":"Hello there, how are you today?"}]}""");
        Assert.True((int)typing["prompt"]! > (int)withSystem["prompt"]!);

        string toolsJson = "[{\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"description\":\"Weather for a city\",\"parameters\":{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}}}}}]";
        var tools = await Usage("{\"model\":\"" + m + "\",\"messages\":[],\"tools\":" + toolsJson + "}");
        Assert.True((int)tools["tools"]! > 10);
        Assert.Equal((int)tools["tools"]!, (int)tools["used"]!);
    }

    [VlmFact]
    public async Task ReserveAndPercentFollowTheWindow()
    {
        string m = await ModelId();
        string big = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 120));
        var u = await Usage($$"""{"model":"{{m}}","maxTokens":100,"messages":[{"role":"user","content":"{{big}}"}]}""");
        int used = (int)u["used"]!;
        Assert.Equal(Math.Max(0, 1024 - used - 100), (int)u["remaining"]!);
        Assert.InRange((double)u["percent"]!, 100.0 * used / 1024 - 0.2, 100.0 * used / 1024 + 0.2);
        Assert.Equal("dropOldest", (string)u["overflowPolicy"]!);
    }

    [VlmFact]
    public async Task AChatAnswerCarriesTheNumbersTheFooterShows()
    {
        string m = await ModelId();
        var r = await _f.Http.PostAsync("/v1/chat/completions", Body($$"""{"model":"{{m}}","temperature":0,"max_tokens":6,"messages":[{"role":"user","content":"Name a fruit."}]}"""));
        var stats = JsonNode.Parse(await r.Content.ReadAsStringAsync())!["stats"]!;
        Assert.True((int)stats["completion_tokens"]! > 0);
        Assert.True((int)stats["prompt_tokens"]! > 0);
        Assert.False(string.IsNullOrEmpty((string)stats["finish_reason"]!));
        Assert.NotNull(stats["draft_accepted"]);
        Assert.NotNull(stats["time_to_first_token"]);
    }
}
