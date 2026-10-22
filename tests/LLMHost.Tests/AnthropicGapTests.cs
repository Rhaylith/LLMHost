using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class AnthropicThinkingTests
{
    private static GenerationOptions Apply(string json) { var g = new GenerationOptions { ReasoningEffort = "high" }; AnthropicThinking.Apply(JsonNode.Parse(json)!, g); return g; }

    [Theory]
    [InlineData("""{"thinking":{"type":"enabled","budget_tokens":512}}""", "low", 512)]
    [InlineData("""{"thinking":{"type":"enabled","budget_tokens":1024}}""", "low", 1024)]
    [InlineData("""{"thinking":{"type":"enabled","budget_tokens":4000}}""", "medium", 4000)]
    [InlineData("""{"thinking":{"type":"enabled","budget_tokens":8192}}""", "medium", 8192)]
    [InlineData("""{"thinking":{"type":"enabled","budget_tokens":32000}}""", "high", 32000)]
    [InlineData("""{"thinking":{"type":"enabled"}}""", "medium", 0)]
    public void ABudgetBecomesAnEffortAndAHardLimit(string body, string effort, int budget)
    {
        var g = Apply(body);
        Assert.Equal(effort, g.ReasoningEffort);
        Assert.Equal(budget, g.ThinkBudgetTokens);
    }

    [Fact]
    public void DisabledSwitchesReasoningOffAndAbsentChangesNothing()
    {
        Assert.Equal("off", Apply("""{"thinking":{"type":"disabled"}}""").ReasoningEffort);
        Assert.Equal("high", Apply("""{"model":"x"}""").ReasoningEffort);
        Assert.Equal("high", Apply("""{"thinking":{"type":"something-new"}}""").ReasoningEffort);
    }

    [Fact]
    public void ASignatureVerifiesOnlyForTheSameText()
    {
        string sig = AnthropicThinking.Sign("step one");
        Assert.True(AnthropicThinking.Verify("step one", sig));
        Assert.False(AnthropicThinking.Verify("step two", sig));
        Assert.False(AnthropicThinking.Verify("step one", "AAAA"));
        Assert.False(AnthropicThinking.Verify("step one", null));
        Assert.Equal(sig, AnthropicThinking.Sign("step one"));                       // stable, so a client can echo it back later
    }

    [Fact]
    public void CacheControlIsRecognisedAnywhere()
    {
        Assert.True(AnthropicThinking.WantsCache(JsonNode.Parse("""{"system":[{"type":"text","text":"x","cache_control":{"type":"ephemeral"}}]}""")!));
        Assert.True(AnthropicThinking.WantsCache(JsonNode.Parse("""{"tools":[{"name":"a","cache_control":{"type":"ephemeral"}}]}""")!));
        Assert.False(AnthropicThinking.WantsCache(JsonNode.Parse("""{"messages":[]}""")!));
    }
}

[Trait("Category", "Unit")]
public class AnthropicRequestShapeTests
{
    [Fact]
    public void ThinkingBlocksInHistoryAreDroppedAndTheTextKept()
    {
        var body = JsonNode.Parse("""
            {"messages":[
              {"role":"user","content":"Hi"},
              {"role":"assistant","content":[{"type":"thinking","thinking":"hmm","signature":"x"},{"type":"redacted_thinking","data":"abc"},{"type":"text","text":"Hello!"}]},
              {"role":"user","content":"And?"}]}
            """)!;
        var msgs = ApiServer.ReadAnthropicMessages(body);
        Assert.Equal(new[] { "user", "assistant", "user" }, msgs.Select(m => m.Role));
        Assert.Equal("Hello!", msgs[1].Content);
        Assert.DoesNotContain("hmm", string.Join("|", msgs.Select(m => m.Content)));
    }

    [Fact]
    public void CacheControlOnSystemAndBlocksIsAcceptedAndIgnored()
    {
        var body = JsonNode.Parse("""
            {"system":[{"type":"text","text":"Be brief.","cache_control":{"type":"ephemeral"}}],
             "messages":[{"role":"user","content":[{"type":"text","text":"Hi","cache_control":{"type":"ephemeral"}}]}],
             "metadata":{"user_id":"u"},"service_tier":"auto","top_k":5,"container":"c"}
            """)!;
        var msgs = ApiServer.ReadAnthropicMessages(body);
        Assert.Equal("Be brief.", msgs[0].Content);
        Assert.Equal("Hi", msgs[1].Content);
    }

    [Fact]
    public void ImagesInsideToolResultsReachTheModelAsASeparateMessage()
    {
        string png = Convert.ToBase64String(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg=="));
        var body = JsonNode.Parse($$$"""
            {"messages":[
              {"role":"user","content":"look"},
              {"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"screenshot","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"done"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"{{{png}}}"}}]}]}]}
            """)!;
        var msgs = ApiServer.ReadAnthropicMessages(body);
        Assert.Contains(msgs, m => m.Images is { Count: 1 });
        Assert.Contains(msgs, m => m.Content.Contains("done"));
    }

    [Theory]
    [InlineData("""{"type":"auto"}""", "auto", false)]
    [InlineData("""{"type":"any"}""", "required", false)]
    [InlineData("""{"type":"none"}""", "none", false)]
    [InlineData("""{"type":"tool","name":"get_weather"}""", "get_weather", false)]
    [InlineData("""{"type":"auto","disable_parallel_tool_use":true}""", "auto", true)]
    public void ToolChoiceMapsToTheNativeModes(string choice, string expected, bool noParallel)
    {
        var body = JsonNode.Parse("{\"tools\":[{\"name\":\"get_weather\",\"description\":\"w\",\"input_schema\":{\"type\":\"object\",\"properties\":{}}}],\"tool_choice\":" + choice + "}")!;
        var setup = OpenAiMapping.ReadAnthropicTools(body, ToolFormat.Generic);
        if (expected == "none") { Assert.Null(setup); return; }                         // "none": the tools are simply not offered
        Assert.NotNull(setup);
        Assert.Equal(expected, setup!.Choice);
        Assert.Equal(!noParallel, setup.Parallel);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class AnthropicGapIntegrationTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public AnthropicGapIntegrationTests(ServerFixture f) => _f = f;

    private async Task<string> ModelId()
    {
        var rt = _f.Host.Models.Loaded.FirstOrDefault(r => r.Identifier == "anth-model")
                 ?? await _f.Host.Models.LoadAsync(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!, new LoadOptions { Device = "cpu", ContextSize = 2048, MaxConcurrent = 1, CacheSlots = 2, Threads = 4, SkipFitCheck = true, Vision = false, Identifier = "anth-model" });
        return rt.Identifier;
    }

    private async Task<(HttpResponseMessage, JsonNode?)> Post(string json, Action<HttpRequestMessage>? tweak = null)
    {
        _f.Host.Settings.AnthropicApi = true;
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add("anthropic-version", "2023-06-01");
        tweak?.Invoke(req);
        var r = await _f.Http.SendAsync(req);
        string text = await r.Content.ReadAsStringAsync();
        return (r, text.StartsWith("{") ? JsonNode.Parse(text) : null);
    }

    [VlmFact]
    public async Task UnknownFieldsAndHeadersAreTolerated()
    {
        string m = await ModelId();
        var (r, j) = await Post($$"""{"model":"{{m}}","max_tokens":4,"temperature":0,"metadata":{"user_id":"u"},"service_tier":"auto","top_k":10,"container":"c","messages":[{"role":"user","content":"Hi"}]}""",
            req => req.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31,tools-2024-04-04"));
        Assert.True(r.IsSuccessStatusCode, j?.ToJsonString());
        Assert.Equal("message", (string)j!["type"]!);
        Assert.NotNull(j["usage"]!["cache_read_input_tokens"]);
        Assert.NotNull(j["usage"]!["cache_creation_input_tokens"]);
    }

    [VlmFact]
    public async Task CacheControlTurnsFreshPromptTokensIntoCacheCreationAndASecondCallReadsThemFromTheCache()
    {
        string m = await ModelId();
        string system = string.Join(" ", Enumerable.Repeat("You are a careful assistant who answers in short sentences.", 12));
        string body = $$$"""{"model":"{{{m}}}","max_tokens":2,"temperature":0,"system":[{"type":"text","text":"{{{system}}}","cache_control":{"type":"ephemeral"}}],"messages":[{"role":"user","content":"Hi"}]}""";
        var (_, first) = await Post(body);
        Assert.True((int)first!["usage"]!["cache_creation_input_tokens"]! > 20);
        Assert.Equal(0, (int)first["usage"]!["input_tokens"]!);
        var (_, second) = await Post(body);
        Assert.True((int)second!["usage"]!["cache_read_input_tokens"]! > 20, second.ToJsonString());
        Assert.True((int)second["usage"]!["cache_creation_input_tokens"]! < (int)first["usage"]!["cache_creation_input_tokens"]!);

        var (_, plain) = await Post($$"""{"model":"{{m}}","max_tokens":2,"temperature":0,"messages":[{"role":"user","content":"A brand new unrelated question about rivers."}]}""");
        Assert.Equal(0, (int)plain!["usage"]!["cache_creation_input_tokens"]!);        // no cache_control, no cache writes
    }

    [VlmFact]
    public async Task ThinkingDisabledAndEnabledAreAccepted()
    {
        string m = await ModelId();
        foreach (var thinking in new[] { """{"type":"disabled"}""", """{"type":"enabled","budget_tokens":1024}""" })
        {
            var (r, j) = await Post($$"""{"model":"{{m}}","max_tokens":1200,"temperature":0,"thinking":{{thinking}},"messages":[{"role":"user","content":"Hi"}]}""");
            Assert.True(r.IsSuccessStatusCode, j?.ToJsonString());
        }
    }

    [VlmFact]
    public async Task AForcedToolIsCalledAndNoneNeverCalls()
    {
        string m = await ModelId();
        const string tool = """[{"name":"get_weather","description":"Weather for a city","input_schema":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}]""";
        var (_, forced) = await Post($$"""{"model":"{{m}}","max_tokens":60,"temperature":0,"tools":{{tool}},"tool_choice":{"type":"tool","name":"get_weather"},"messages":[{"role":"user","content":"What is the weather in Paris?"}]}""");
        var use = forced!["content"]!.AsArray().FirstOrDefault(b => (string)b!["type"]! == "tool_use");
        Assert.NotNull(use);
        Assert.Equal("get_weather", (string)use!["name"]!);
        Assert.Equal("tool_use", (string)forced["stop_reason"]!);

        var (_, none) = await Post($$"""{"model":"{{m}}","max_tokens":20,"temperature":0,"tools":{{tool}},"tool_choice":{"type":"none"},"messages":[{"role":"user","content":"What is the weather in Paris?"}]}""");
        Assert.DoesNotContain(none!["content"]!.AsArray(), b => (string)b!["type"]! == "tool_use");
    }

    [VlmFact]
    public async Task StreamingEndsWithTheFullUsage()
    {
        string m = await ModelId();
        _f.Host.Settings.AnthropicApi = true;
        var r = await _f.Http.PostAsync("/v1/messages", new StringContent($$"""{"model":"{{m}}","max_tokens":4,"stream":true,"messages":[{"role":"user","content":"Hi"}]}""", Encoding.UTF8, "application/json"));
        string text = await r.Content.ReadAsStringAsync();
        int i = text.IndexOf("event: message_delta", StringComparison.Ordinal);
        Assert.True(i > 0);
        string data = text[(text.IndexOf("data: ", i, StringComparison.Ordinal) + 6)..].Split('\n')[0];
        var usage = JsonNode.Parse(data)!["usage"]!;
        Assert.True((int)usage["output_tokens"]! > 0);
        Assert.NotNull(usage["cache_read_input_tokens"]);
        Assert.NotNull(usage["cache_creation_input_tokens"]);
    }
}
