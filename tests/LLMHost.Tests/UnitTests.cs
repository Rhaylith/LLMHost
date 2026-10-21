using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

public class GgufTests
{
    [Fact]
    public void ReadsMetadataAndTensorSizes()
    {
        string path = GgufBuilder.WriteFile("gguf/ok.gguf", GgufBuilder.Build(blocks: 4, embd: 64, heads: 4, kvHeads: 2, ctx: 4096, vocab: 7, chatTemplate: "{{ x }}<tool_call>"));
        var info = GgufInfo.Read(path, out var tensors);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal(4, info.BlockCount);
        Assert.Equal(4096, info.ContextLength);
        Assert.Equal(64, info.EmbeddingLength);
        Assert.Equal(4, info.HeadCount);
        Assert.Equal(2, info.HeadCountKv);
        Assert.Equal(7, info.VocabSize);
        Assert.True(info.HasChatTemplate);
        Assert.Equal("Q4_0", info.Quantization);
        Assert.Equal(3 + 4 * 2, tensors.Count);
        Assert.All(tensors, t => Assert.Equal(256, t.Size));
    }

    [Fact]
    public void TruncatedFileIsReportedAsDamaged()
    {
        string path = GgufBuilder.WriteFile("gguf/trunc.gguf", GgufBuilder.Build(truncate: true));
        var ex = Assert.Throws<GgufFormatException>(() => GgufInfo.Read(path));
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void BadMagicIsRejected()
    {
        string path = GgufBuilder.WriteFile("gguf/junk.gguf", new byte[200]);
        Assert.Throws<GgufFormatException>(() => GgufInfo.Read(path));
        Assert.Null(GgufInfo.TryRead(path));
    }

    [Fact]
    public void ShardFilesAreEnumerated()
    {
        var files = GgufInfo.ShardFiles(@"C:\m\big-00001-of-00003.gguf");
        Assert.Equal(3, files.Count);
        Assert.EndsWith("big-00003-of-00003.gguf", files[2]);
        Assert.Single(GgufInfo.ShardFiles(@"C:\m\single.gguf"));
    }
}

public class TextScannerTests
{
    private static (string content, string reasoning) Run(TextScanner s, params string[] pieces)
    {
        var c = new System.Text.StringBuilder(); var r = new System.Text.StringBuilder();
        foreach (var p in pieces)
            foreach (var seg in s.Feed(p)) (seg.Kind == SegmentKind.Reasoning ? r : c).Append(seg.Text);
        foreach (var seg in s.Flush()) (seg.Kind == SegmentKind.Reasoning ? r : c).Append(seg.Text);
        return (c.ToString(), r.ToString());
    }

    [Fact]
    public void SplitsThinkFromAnswerEvenWhenTagsAreSplit()
    {
        var (c, r) = Run(new TextScanner(Array.Empty<string>()), "<thi", "nk>let me ", "think</th", "ink>\n\nThe answer");
        Assert.Equal("let me think", r);
        Assert.Equal("The answer", c);
    }

    [Fact]
    public void StartsInsideThinkWhenPromptOpenedIt()
    {
        var (c, r) = Run(new TextScanner(Array.Empty<string>(), startInThink: true), "reasoning here</think>done");
        Assert.Equal("reasoning here", r);
        Assert.Equal("done", c);
    }

    [Fact]
    public void StopStringCutsTheAnswerAndIsNeverLeaked()
    {
        var s = new TextScanner(new[] { "###" });
        var (c, _) = Run(s, "hello ##", "# world");
        Assert.Equal("hello ", c);
        Assert.True(s.Stopped);
        Assert.Equal("###", s.MatchedStop);
    }

    [Fact]
    public void PartialMarkerAtTheEndIsFlushedAsText()
    {
        var (c, _) = Run(new TextScanner(new[] { "STOP" }), "value <th");
        Assert.Equal("value <th", c);
    }
}

public class ReasoningTests
{
    [Theory]
    [InlineData("off", "off")]
    [InlineData("none", "off")]
    [InlineData("minimal", "low")]
    [InlineData("HIGH", "high")]
    [InlineData("true", "on")]
    [InlineData("bogus", null)]
    public void EffortIsNormalised(string input, string? expected) => Assert.Equal(expected, Reasoning.NormalizeEffort(input));

    [Fact]
    public void OffAddsEmptyThinkBlockForSwitchableTemplates()
    {
        string p = Reasoning.ApplyToPrompt("<|im_start|>assistant\n", ReasoningStyle.ThinkSwitch, "off");
        Assert.EndsWith("<think>\n\n</think>\n\n", p);
        Assert.Equal("x", Reasoning.ApplyToPrompt("x", ReasoningStyle.None, "off"));
        Assert.Equal("x", Reasoning.ApplyToPrompt("x", ReasoningStyle.ThinkSwitch, "high"));
    }

    [Fact]
    public void DetectsTemplates()
    {
        Assert.Equal(ReasoningStyle.ThinkSwitch, Reasoning.Detect("{% if enable_thinking %}"));
        Assert.Equal(ReasoningStyle.Harmony, Reasoning.Detect("<|channel|>analysis"));
        Assert.Equal(ReasoningStyle.None, Reasoning.Detect("plain chatml"));
        Assert.True(Reasoning.EndsInsideThink("<|assistant|><think>\n"));
        Assert.False(Reasoning.EndsInsideThink("<think>x</think>done"));
    }

    [Fact]
    public void ThinkingBudgetFollowsEffort()
    {
        Assert.Equal(512, Reasoning.Budget("low"));
        Assert.Equal(2048, Reasoning.Budget("medium"));
        Assert.Equal(-1, Reasoning.Budget("high"));
        Assert.Equal("a", Reasoning.StripThink("<think>hidden</think>a"));
    }
}

public class SchemaGrammarTests
{
    [Fact]
    public void ObjectSchemaProducesRootAndKeys()
    {
        string g = SchemaGrammar.FromSchema("""{"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"}},"required":["name"]}""");
        Assert.StartsWith("root ::=", g);
        Assert.Contains("\"\\\"name\\\"\"", g);
        Assert.Contains("integer", g);
    }

    [Fact]
    public void EnumBecomesAlternatives()
    {
        string g = SchemaGrammar.FromSchema("""{"enum":["red","green"]}""");
        Assert.Contains("red", g);
        Assert.Contains("green", g);
        Assert.Contains("|", g);
    }

    [Fact]
    public void ToolCallGrammarForcesTheFunctionName()
    {
        var fns = new List<JsonObject> { JsonNode.Parse("""{"name":"get_weather","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}""")!.AsObject() };
        string g = ToolFormats.Grammar(ToolFormat.Hermes, fns, "get_weather", allowMany: false)!;
        Assert.StartsWith("root ::=", g);
        Assert.Contains("<tool_call>", g);
        Assert.Contains("callobj ::=", g);
        Assert.Contains("get_weather", g);
        Assert.Null(ToolFormats.Grammar(ToolFormat.Hermes, fns, "missing", false));
    }
}

public class ToolCallingTests
{
    [Theory]
    [InlineData("{{ '<tool_call>' }}", ToolFormat.Hermes)]
    [InlineData("[AVAILABLE_TOOLS]", ToolFormat.Mistral)]
    [InlineData("<|python_tag|>", ToolFormat.Llama3)]
    [InlineData("plain", ToolFormat.Generic)]
    public void FormatIsDetectedFromTheTemplate(string template, ToolFormat expected) => Assert.Equal(expected, ToolFormats.Detect(template));

    [Fact]
    public void HermesCallsAreParsedWithSurroundingText()
    {
        var (content, calls) = ToolStreamParser.ParseAll(ToolFormat.Hermes,
            "Let me check.\n<tool_call>\n{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"}}\n</tool_call>\n<tool_call>\n{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Rome\"}}\n</tool_call>");
        Assert.Equal("Let me check.", content);
        Assert.Equal(2, calls.Count);
        Assert.Equal("get_weather", calls[0].Name);
        Assert.Contains("Paris", calls[0].Arguments);
        Assert.Contains("Rome", calls[1].Arguments);
    }

    [Fact]
    public void MistralAndLlamaFormats()
    {
        var (_, m) = ToolStreamParser.ParseAll(ToolFormat.Mistral, "[TOOL_CALLS][{\"name\":\"a\",\"arguments\":{\"x\":1}},{\"name\":\"b\",\"arguments\":{}}]");
        Assert.Equal(new[] { "a", "b" }, m.Select(c => c.Name));
        var (_, l) = ToolStreamParser.ParseAll(ToolFormat.Llama3, "<|python_tag|>{\"name\": \"f\", \"parameters\": {\"q\": \"hi\"}}");
        Assert.Single(l);
        Assert.Contains("hi", l[0].Arguments);
    }

    [Fact]
    public void PlainJsonAnswerIsNotMistakenForACall()
    {
        var (content, calls) = ToolStreamParser.ParseAll(ToolFormat.Generic, "{\"answer\": 42}");
        Assert.Empty(calls);
        Assert.Equal("{\"answer\": 42}", content);
    }

    [Fact]
    public void ArgumentsStreamAsTheyAreGenerated()
    {
        var p = new ToolStreamParser(ToolFormat.Hermes);
        var events = new List<ToolEvent>();
        foreach (var piece in new[] { "<tool_", "call>{\"name\": \"f", "n\", \"argu", "ments\": {\"a\": ", "12", "}}</tool_call>" })
            events.AddRange(p.Feed(piece));
        events.AddRange(p.Finish());
        var start = Assert.Single(events.OfType<CallStartEvent>());
        Assert.Equal("fn", start.Name);
        string args = string.Concat(events.OfType<ArgsDeltaEvent>().Select(e => e.Text));
        Assert.Equal("{\"a\": 12}", args);
        Assert.Single(events.OfType<CallEndEvent>());
        Assert.Empty(events.OfType<ContentEvent>());
    }

    [Fact]
    public void HistoryIsRenderedInTheModelsOwnFormat()
    {
        string echo = ToolFormats.EchoCalls(ToolFormat.Hermes, null, new[] { ("f", (JsonNode?)JsonNode.Parse("{\"a\":1}")) });
        Assert.Contains("<tool_call>", echo);
        var (role, content) = ToolFormats.Result(ToolFormat.Hermes, "f", "ok");
        Assert.Equal("user", role);
        Assert.Contains("<tool_response>", content);
    }
}

public class MappingTests
{
    private static JsonNode J(string s) => JsonNode.Parse(s)!;

    [Fact]
    public void GenerationParametersAreRead()
    {
        var g = OpenAiMapping.ReadGeneration(J("""
            {"temperature":0.2,"top_p":0.5,"max_completion_tokens":77,"stop":["a","b"],"seed":9,"logprobs":true,"top_logprobs":3,
             "response_format":{"type":"json_schema","json_schema":{"schema":{"type":"object"}}},"reasoning_effort":"low"}
            """), new GenerationOptions());
        Assert.Equal(0.2f, g.Temperature);
        Assert.Equal(0.5f, g.TopP);
        Assert.Equal(77, g.MaxTokens);
        Assert.Equal(new[] { "a", "b" }, g.Stop);
        Assert.Equal(9u, g.Seed);
        Assert.True(g.Logprobs);
        Assert.Equal(3, g.TopLogprobs);
        Assert.NotNull(g.JsonSchema);
        Assert.Equal("low", g.ReasoningEffort);
    }

    [Theory]
    [InlineData("""{"enable_thinking":false}""", "off")]
    [InlineData("""{"reasoning":{"effort":"high"}}""", "high")]
    [InlineData("""{"thinking":{"type":"enabled","budget_tokens":1024}}""", "low")]
    [InlineData("""{"thinking":{"type":"disabled"}}""", "off")]
    public void ReasoningDialects(string body, string expected)
    {
        var g = new GenerationOptions();
        OpenAiMapping.ReadReasoning(J(body), g);
        Assert.Equal(expected, g.ReasoningEffort);
    }

    [Fact]
    public void ToolChoiceVariants()
    {
        string tools = """[{"type":"function","function":{"name":"f","parameters":{"type":"object"}}}]""";
        Assert.Null(OpenAiMapping.ReadTools(J("{\"tools\":" + tools + ",\"tool_choice\":\"none\"}"), ToolFormat.Hermes));
        var req = OpenAiMapping.ReadTools(J("{\"tools\":" + tools + ",\"tool_choice\":\"required\"}"), ToolFormat.Hermes)!;
        Assert.True(req.MustCall);
        Assert.Null(req.ForcedName);
        var named = OpenAiMapping.ReadTools(J("{\"tools\":" + tools + ",\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"f\"}}}"), ToolFormat.Hermes)!;
        Assert.Equal("f", named.ForcedName);
        Assert.Throws<ArgumentException>(() => OpenAiMapping.ReadTools(J("{\"tools\":" + tools + ",\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"zz\"}}}"), ToolFormat.Hermes));
    }

    [Fact]
    public void ToolHistoryUsesTheModelFormat()
    {
        var msgs = OpenAiMapping.ReadMessages(J("""
            {"messages":[{"role":"user","content":"weather?"},
              {"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Rome\"}"}}]},
              {"role":"tool","tool_call_id":"c1","content":"sunny"}]}
            """), ToolFormat.Hermes);
        Assert.Equal(3, msgs.Count);
        Assert.Contains("<tool_call>", msgs[1].Content);
        Assert.Contains("get_weather", msgs[1].Content);
        Assert.Contains("<tool_response>", msgs[2].Content);
        Assert.Contains("sunny", msgs[2].Content);
    }

    [Fact]
    public void ImagesAreCollectedFromDataUrls()
    {
        string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        var msgs = OpenAiMapping.ReadMessages(J("{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"what?\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64," + png + "\"}}]}]}"));
        Assert.Single(msgs);
        Assert.Equal("what?", msgs[0].Content);
        Assert.Single(msgs[0].Images!);
    }
}

public class FitTests
{
    [Fact]
    public void EstimateCountsWeightsKvAndFitsOnTheCpu()
    {
        string path = GgufBuilder.WriteFile("fit/small.gguf", GgufBuilder.Build(blocks: 4, embd: 64, heads: 4, kvHeads: 2, ctx: 2048));
        var model = new ModelLibrary().Find(path)!;
        var e = FitEstimator.Estimate(model, new LoadOptions { Device = "cpu", ContextSize = 1024 }, gpuBackend: false);
        Assert.Equal(11 * 256, e.WeightsBytes);
        Assert.True(e.KvCacheBytes > 0);
        Assert.Equal(0, e.OffloadedLayers);
        Assert.Equal("fits", e.Verdict);
        Assert.Contains(e.Devices, d => d.Name == "System memory");
    }

    [Fact]
    public void KvCacheGrowsWithContextAndShrinksWithQuantization()
    {
        string path = GgufBuilder.WriteFile("fit/kv.gguf", GgufBuilder.Build());
        var model = new ModelLibrary().Find(path)!;
        var small = FitEstimator.Estimate(model, new LoadOptions { ContextSize = 512 }, gpuBackend: false);
        var big = FitEstimator.Estimate(model, new LoadOptions { ContextSize = 2048 }, gpuBackend: false);
        var q8 = FitEstimator.Estimate(model, new LoadOptions { ContextSize = 2048, KvCacheTypeK = "q8_0", KvCacheTypeV = "q8_0" }, gpuBackend: false);
        Assert.True(big.KvCacheBytes > small.KvCacheBytes * 3);
        Assert.True(q8.KvCacheBytes < big.KvCacheBytes);
    }

    [Fact]
    public void GpuSharesKeepASmallModelOnTheFirstGpuAndSpillInPriorityOrder()
    {
        var o = new LoadOptions();
        var fits = GpuPlanner.Shares(o, 2, new double[] { 100, 100 }, 60, "layer");
        Assert.Equal(new[] { 1.0, 0.0 }, fits);
        var spill = GpuPlanner.Shares(o, 2, new double[] { 100, 100 }, 150, "layer");
        Assert.Equal(100.0 / 150, spill[0], 3);
        Assert.Equal(50.0 / 150, spill[1], 3);
        var explicitSplit = GpuPlanner.Shares(new LoadOptions { TensorSplit = new List<float> { 3, 1 } }, 2, new double[] { 100, 100 }, 60, "layer");
        Assert.Equal(0.75, explicitSplit[0], 3);
        var none = GpuPlanner.Shares(o, 2, new double[] { 100, 100 }, 500, "none");
        Assert.Equal(1.0, none[0]);
    }
}

public class DocumentTests
{
    [Fact]
    public void ExtractsDocxParagraphs()
    {
        var (text, _) = DocumentExtractor.Extract("a.docx", DocBuilder.Docx("First paragraph.", "Second & last."));
        Assert.Equal("First paragraph.\nSecond & last.", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtractsPdfTextThroughToUnicodeMaps(bool compressed)
    {
        var (text, pages) = DocumentExtractor.Extract("a.pdf", DocBuilder.Pdf("Hello PDF World", compressed));
        Assert.Equal("Hello PDF World", text.Trim());
        Assert.Equal(1, pages);
    }

    [Fact]
    public void RejectsBinaryAndEncryptedFiles()
    {
        Assert.Throws<InvalidDataException>(() => DocumentExtractor.Extract("x.bin", new byte[] { 0, 1, 2, 0, 0, 0, 9, 0 }));
        Assert.Throws<InvalidDataException>(() => DocumentExtractor.Extract("e.pdf", System.Text.Encoding.Latin1.GetBytes("%PDF-1.4 /Encrypt 5 0 R")));
    }

    [Fact]
    public void Bm25RanksTheChunkWithTheAnswerFirst()
    {
        var doc = new StoredDocument { Id = "d1", Name = "n.txt" };
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append($"Paragraph {i} is about gardening and soil.\n\n");
        sb.Append("The secret launch code is TANGERINE-42 for the Zephyr project.\n\n");
        for (int i = 0; i < 40; i++) sb.Append($"Paragraph {i + 100} is about cooking pasta.\n\n");
        doc.Text = sb.ToString();
        var chunks = Retrieval.Split(doc, target: 300);
        Assert.True(chunks.Count > 3);
        var scores = Retrieval.Bm25(chunks, "What is the secret launch code of Zephyr?");
        int best = Array.IndexOf(scores, scores.Max());
        Assert.Contains("TANGERINE-42", chunks[best].Text);
    }
}

public class AnthropicBlockTests
{
    [Fact]
    public void BookkeepingBlocksAreDroppedAndTheQuestionComesLast()
    {
        var text = LLMHostProject.Server.ApiServer.OrderTextBlocks(new[]
        {
            "tell me about std::vector",
            "<system-reminder>\nThe user's email is x\n</system-reminder>",
            "<total_tokens>15000000 tokens left</total_tokens>",
        });
        Assert.StartsWith("<system-reminder>", text);
        Assert.EndsWith("tell me about std::vector", text);
        Assert.DoesNotContain("total_tokens", text);
    }

    [Fact]
    public void PlainSingleBlockIsUnchanged() =>
        Assert.Equal("hello", LLMHostProject.Server.ApiServer.OrderTextBlocks(new[] { "hello" }));
}

public class ClaudeCodeShapeTests
{
    [Fact]
    public void SystemRoleMessagesBecomeSystemPromptNotUserTurns()
    {
        var body = JsonNode.Parse("""
            {"system":[{"type":"text","text":"You are a coding assistant."}],
             "messages":[
               {"role":"user","content":[{"type":"text","text":"<system-reminder>ctx</system-reminder>"},{"type":"text","text":"tell me about std::vector"}]},
               {"role":"system","content":"# Environment\nWorking directory: D:/code"},
               {"role":"system","content":[{"type":"text","text":"<total_tokens>999 tokens left</total_tokens>"}]}]}
            """)!;
        var msgs = LLMHostProject.Server.ApiServer.ReadAnthropicMessages(body);
        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Contains("coding assistant", msgs[0].Content);
        Assert.Contains("Working directory", msgs[0].Content);
        Assert.DoesNotContain("total_tokens", msgs[0].Content);
        Assert.Equal("user", msgs[1].Role);
        Assert.EndsWith("tell me about std::vector", msgs[1].Content);
    }

    [Fact]
    public void FlashAttentionIsAutomaticUnlessForced()
    {
        Assert.Null(new LoadOptions().FlashAttentionSetting);
        Assert.True(new LoadOptions { FlashAttentionMode = "on" }.FlashAttentionSetting);
        Assert.True(new LoadOptions { FlashAttention = true }.FlashAttentionSetting);
        Assert.False(new LoadOptions { FlashAttentionMode = "off", FlashAttention = true }.FlashAttentionSetting);
    }
}
