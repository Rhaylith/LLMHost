using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class LogprobsFormatTests
{
    private static TokenLogprob Tok(string text, double lp, bool reasoning = false, params (string Text, double Lp)[] top) =>
        new(text, lp, Encoding.UTF8.GetBytes(text), top.Select(t => new TopLogprob(t.Text, t.Lp, Encoding.UTF8.GetBytes(t.Text))).ToList(), reasoning);

    [Fact]
    public void ChatShapeHasTokenLogprobBytesTopAndRefusal()
    {
        var o = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(LogprobsFormat.Chat(new[] { Tok("Hi", -0.1, false, ("Hi", -0.1), ("Hello", -2.5)) })))!;
        var item = o["content"]![0]!;
        Assert.Equal("Hi", (string)item["token"]!);
        Assert.Equal(-0.1, (double)item["logprob"]!, 6);
        Assert.Equal(new[] { 72, 105 }, item["bytes"]!.AsArray().Select(b => (int)b!).ToArray());
        Assert.Equal(2, item["top_logprobs"]!.AsArray().Count);
        Assert.Equal("Hello", (string)item["top_logprobs"]![1]!["token"]!);
        Assert.Null(o["refusal"]);
    }

    [Fact]
    public void ReasoningTokensAreNotPartOfTheAnswerLogprobs()
    {
        Assert.Null(LogprobsFormat.Chat(new[] { Tok("think", -1, reasoning: true) }));
        var o = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(LogprobsFormat.Chat(new[] { Tok("think", -1, reasoning: true), Tok("answer", -0.5) })))!;
        Assert.Single(o["content"]!.AsArray());
        Assert.Single(LogprobsFormat.Items(new[] { Tok("think", -1, reasoning: true), Tok("answer", -0.5) }));
    }

    [Fact]
    public void ACharacterSplitOverTokensKeepsItsRawBytes()
    {
        // "é" is C3 A9; a tokenizer may emit the two bytes as separate tokens: the byte arrays must stay raw, not become "U+FFFD"
        var a = new TokenLogprob("�", -1, new byte[] { 0xC3 }, new List<TopLogprob>());
        var b = new TokenLogprob("�", -1, new byte[] { 0xA9 }, new List<TopLogprob>());
        var o = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(LogprobsFormat.Chat(new[] { a, b })))!;
        var all = o["content"]!.AsArray().SelectMany(t => t!["bytes"]!.AsArray().Select(x => (byte)(int)x!)).ToArray();
        Assert.Equal("é", Encoding.UTF8.GetString(all));
    }

    [Fact]
    public void LegacyShapeHasOffsetsAndTopDictionaries()
    {
        var o = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(LogprobsFormat.Text(new[] { Tok("ab", -1, false, ("ab", -1), ("x", -3)), Tok("c", -2) })))!;
        Assert.Equal(new[] { "ab", "c" }, o["tokens"]!.AsArray().Select(t => (string)t!).ToArray());
        Assert.Equal(new[] { 0, 2 }, o["text_offset"]!.AsArray().Select(t => (int)t!).ToArray());
        Assert.Equal(-3, (double)o["top_logprobs"]![0]!["x"]!, 6);
    }


    [Fact]
    public void LeadingWhitespaceIsDroppedLikeInTheAnswerText()
    {
        var trim = new LeadingTrim();
        var first = trim.Apply(new[] { Tok(" ", -1), Tok("\n", -1), Tok(" The", -0.5), Tok(" fruit", -0.2) });
        Assert.Equal(new[] { "The", " fruit" }, first.Select(t => t.Token).ToArray());
        Assert.Equal(new byte[] { (byte)'T', (byte)'h', (byte)'e' }, first[0].Bytes);
        // later chunks of the same stream are left alone, spaces included
        Assert.Equal(" more", trim.Apply(new[] { Tok(" more", -1) })[0].Token);
        // reasoning tokens neither count nor get trimmed
        var t2 = new LeadingTrim().Apply(new[] { Tok(" think", -1, reasoning: true), Tok(" Hi", -1) });
        Assert.Equal(" think", t2[0].Token);
        Assert.Equal("Hi", t2[1].Token);
    }
    [Fact]
    public void ResponsesItemsAreAPlainArray()
    {
        var arr = LogprobsFormat.Items(new[] { Tok("a", -1, false, ("a", -1)) });
        Assert.Equal("a", (string)arr[0]!["token"]!);
        Assert.Equal(97, (int)arr[0]!["bytes"]![0]!);
        Assert.Equal("a", (string)arr[0]!["top_logprobs"]![0]!["token"]!);
    }

    [Fact]
    public void RequestFieldsEnableLogprobsAndAreClamped()
    {
        var chat = OpenAiMapping.ReadGeneration(JsonNode.Parse("""{"logprobs":true,"top_logprobs":50}""")!, new GenerationOptions());
        Assert.True(chat.Logprobs);
        Assert.Equal(20, chat.TopLogprobs);

        var legacy = OpenAiMapping.ReadGeneration(JsonNode.Parse("""{"logprobs":5}""")!, new GenerationOptions());
        Assert.True(legacy.Logprobs);
        Assert.Equal(5, legacy.TopLogprobs);

        var responses = OpenAiMapping.ReadGeneration(JsonNode.Parse("""{"include":["message.output_text.logprobs"],"top_logprobs":3}""")!, new GenerationOptions());
        Assert.True(responses.Logprobs);
        Assert.Equal(3, responses.TopLogprobs);

        Assert.False(OpenAiMapping.ReadGeneration(JsonNode.Parse("""{"include":["reasoning.encrypted_content"]}""")!, new GenerationOptions()).Logprobs);
        Assert.False(OpenAiMapping.ReadGeneration(JsonNode.Parse("""{"logprobs":false,"top_logprobs":4}""")!, new GenerationOptions()).Logprobs);
    }
}

/// <summary>The HTTP contract of log-probabilities and several choices, against a real (small) model.</summary>
[Trait("Category", "Integration")]
[Collection("Models")]
public class LogprobsApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public LogprobsApiTests(ServerFixture f) => _f = f;

    private async Task<string> ModelId()
    {
        var path = Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!;
        var rt = _f.Host.Models.Loaded.FirstOrDefault(r => r.Identifier == "lp-model")
                 ?? await _f.Host.Models.LoadAsync(path, new LoadOptions { Device = "cpu", ContextSize = 2048, MaxConcurrent = 2, CacheSlots = 3, Threads = 8, SkipFitCheck = true, Vision = false, Identifier = "lp-model" });
        return rt.Identifier;
    }

    private static JsonObject Chat(string model, object extra)
    {
        var o = new JsonObject
        {
            ["model"] = model, ["temperature"] = 0, ["max_tokens"] = 10,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Name a fruit." }),
        };
        foreach (var kv in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(extra))!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    private async Task<JsonNode> Post(string path, JsonObject body)
    {
        var r = await _f.Http.PostAsJsonAsync(path, body);
        string text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!;
    }

    private async Task<List<JsonNode>> Stream(string path, JsonObject body, bool requireDone = true)
    {
        body["stream"] = true;
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var resp = await _f.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(resp.IsSuccessStatusCode);
        var chunks = new List<JsonNode>();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        string? line; bool done = false;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            string data = line[6..];
            if (data == "[DONE]") { done = true; break; }
            chunks.Add(JsonNode.Parse(data)!);
        }
        if (requireDone) Assert.True(done, "the stream must end with [DONE]");
        return chunks;
    }

    [VlmFact]
    public async Task ChatLogprobsMatchTheTextAndRespectTopLogprobs()
    {
        string model = await ModelId();
        var j = await Post("/v1/chat/completions", Chat(model, new { logprobs = true, top_logprobs = 3 }));
        var choice = j["choices"]![0]!;
        string text = (string)choice["message"]!["content"]!;
        var content = choice["logprobs"]!["content"]!.AsArray();

        Assert.NotEmpty(content);
        Assert.All(content, t => Assert.InRange(t!["top_logprobs"]!.AsArray().Count, 1, 3));
        Assert.All(content, t => Assert.True((double)t!["logprob"]! <= 0));
        // greedy: the chosen token is the best candidate
        Assert.All(content, t => Assert.Equal(((string)t!["token"]!).TrimStart(), ((string)t["top_logprobs"]![0]!["token"]!).TrimStart()));   // the first token lost its leading space like the text did
        // the bytes of all tokens spell the answer
        var bytes = content.SelectMany(t => t!["bytes"]!.AsArray().Select(b => (byte)(int)b!)).ToArray();
        Assert.Equal(text, Encoding.UTF8.GetString(bytes));
        Assert.Null(choice["logprobs"]!["refusal"]);
    }

    [VlmFact]
    public async Task TopLogprobsAreCappedAtTwenty()
    {
        string model = await ModelId();
        var j = await Post("/v1/chat/completions", Chat(model, new { logprobs = true, top_logprobs = 50, max_tokens = 3 }));
        Assert.All(j["choices"]![0]!["logprobs"]!["content"]!.AsArray(), t => Assert.True(t!["top_logprobs"]!.AsArray().Count <= 20));
    }

    [VlmFact]
    public async Task WithoutLogprobsTheFieldIsNull()
    {
        string model = await ModelId();
        var j = await Post("/v1/chat/completions", Chat(model, new { }));
        Assert.Null(j["choices"]![0]!["logprobs"]);
    }

    [VlmFact]
    public async Task SeveralChoicesGetIndicesAndSummedUsage()
    {
        string model = await ModelId();
        var j = await Post("/v1/chat/completions", Chat(model, new { n = 3, temperature = 0.8, seed = 5 }));
        var choices = j["choices"]!.AsArray();
        Assert.Equal(3, choices.Count);
        Assert.Equal(new[] { 0, 1, 2 }, choices.Select(c => (int)c!["index"]!).ToArray());
        Assert.All(choices, c => Assert.False(string.IsNullOrEmpty((string)c!["message"]!["content"]!)));
        Assert.True((int)j["usage"]!["completion_tokens"]! >= 3);
        Assert.Equal((int)j["usage"]!["prompt_tokens"]! + (int)j["usage"]!["completion_tokens"]!, (int)j["usage"]!["total_tokens"]!);
    }

    [VlmFact]
    public async Task StreamedChoicesCarryTheirIndexLogprobsAndAFinalUsageChunk()
    {
        string model = await ModelId();
        var chunks = await Stream("/v1/chat/completions", Chat(model, new { n = 2, logprobs = true, top_logprobs = 2, stream_options = new { include_usage = true } }));

        // every chunk but the last has choices; each choice index starts with the role and ends with a finish reason
        var usageChunk = chunks[^1];
        Assert.Empty(usageChunk["choices"]!.AsArray());
        Assert.True((int)usageChunk["usage"]!["completion_tokens"]! > 0);
        var body = chunks.Take(chunks.Count - 1).ToList();
        Assert.All(body, c => Assert.True(c["usage"] == null));

        foreach (int index in new[] { 0, 1 })
        {
            var mine = body.Where(c => (int)c["choices"]![0]!["index"]! == index).ToList();
            Assert.Equal("assistant", (string)mine.First()["choices"]![0]!["delta"]!["role"]!);
            Assert.False(string.IsNullOrEmpty((string)mine.Last()["choices"]![0]!["finish_reason"]!));
            var withLogprobs = mine.Where(c => c["choices"]![0]!["logprobs"] != null).ToList();
            Assert.NotEmpty(withLogprobs);
            // text and logprob bytes agree
            string text = string.Concat(mine.Select(c => (string?)c["choices"]![0]!["delta"]!["content"] ?? ""));
            var bytes = withLogprobs.SelectMany(c => c["choices"]![0]!["logprobs"]!["content"]!.AsArray().SelectMany(t => t!["bytes"]!.AsArray().Select(b => (byte)(int)b!))).ToArray();
            Assert.Equal(text, Encoding.UTF8.GetString(bytes));
        }
    }

    [VlmFact]
    public async Task LegacyCompletionsUseTheTokenArrays()
    {
        string model = await ModelId();
        var j = await Post("/v1/completions", new JsonObject { ["model"] = model, ["prompt"] = "The capital of France is", ["max_tokens"] = 5, ["temperature"] = 0, ["logprobs"] = 2 });
        var lp = j["choices"]![0]!["logprobs"]!;
        int n = lp["tokens"]!.AsArray().Count;
        Assert.True(n > 0);
        Assert.Equal(n, lp["token_logprobs"]!.AsArray().Count);
        Assert.Equal(n, lp["text_offset"]!.AsArray().Count);
        Assert.Equal(n, lp["top_logprobs"]!.AsArray().Count);
    }

    [VlmFact]
    public async Task ResponsesApiReturnsLogprobsOnTheOutputText()
    {
        string model = await ModelId();
        var body = new JsonObject
        {
            ["model"] = model, ["input"] = "Name a fruit.", ["max_output_tokens"] = 8, ["temperature"] = 0,
            ["include"] = new JsonArray("message.output_text.logprobs"), ["top_logprobs"] = 2,
        };
        var j = await Post("/v1/responses", body);
        var part = j["output"]!.AsArray().First(o => (string)o!["type"]! == "message")!["content"]![0]!;
        var lp = part["logprobs"]!.AsArray();
        Assert.NotEmpty(lp);
        Assert.InRange(lp[0]!["top_logprobs"]!.AsArray().Count, 1, 2);
        var bytes = lp.SelectMany(t => t!["bytes"]!.AsArray().Select(b => (byte)(int)b!)).ToArray();
        Assert.Equal((string)part["text"]!, Encoding.UTF8.GetString(bytes));

        // streamed: the deltas carry the same information and the final text agrees
        var events = await Stream("/v1/responses", body, requireDone: false);   // Responses streams end when the connection closes
        var deltas = events.Where(e => (string?)e["type"] == "response.output_text.delta").ToList();
        Assert.NotEmpty(deltas);
        Assert.All(deltas, d => Assert.NotNull(d["logprobs"]));
        string streamed = string.Concat(deltas.Select(d => (string)d["delta"]!));
        var sbytes = deltas.SelectMany(d => d["logprobs"]!.AsArray().SelectMany(t => t!["bytes"]!.AsArray().Select(b => (byte)(int)b!))).ToArray();
        Assert.Equal(streamed, Encoding.UTF8.GetString(sbytes));
        var done = events.First(e => (string?)e["type"] == "response.output_text.done");
        Assert.NotNull(done["logprobs"]);
    }

    [VlmFact]
    public async Task NativeChatReturnsLogprobsWhenAsked()
    {
        string model = await ModelId();
        var j = await Post("/api/v1/chat", new JsonObject
        {
            ["model"] = model, ["input"] = "Name a fruit.", ["max_output_tokens"] = 6, ["temperature"] = 0, ["logprobs"] = true, ["top_logprobs"] = 2,
        });
        var message = j["output"]!.AsArray().First(o => (string)o!["type"]! == "message")!;
        Assert.NotEmpty(message["logprobs"]!.AsArray());
        var bytes = message["logprobs"]!.AsArray().SelectMany(t => t!["bytes"]!.AsArray().Select(b => (byte)(int)b!)).ToArray();
        Assert.Equal((string)message["content"]!, Encoding.UTF8.GetString(bytes));
    }
}
