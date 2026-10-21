using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class ContextOverflowUnitTests
{
    [Theory]
    [InlineData(null, OverflowPolicy.DropOldest)]
    [InlineData("", OverflowPolicy.DropOldest)]
    [InlineData("dropOldest", OverflowPolicy.DropOldest)]
    [InlineData("truncateMiddle", OverflowPolicy.TruncateMiddle)]
    [InlineData("truncate_middle", OverflowPolicy.TruncateMiddle)]
    [InlineData("rolling-window", OverflowPolicy.RollingWindow)]
    [InlineData("ROLLINGWINDOW", OverflowPolicy.RollingWindow)]
    [InlineData("stopAtLimit", OverflowPolicy.StopAtLimit)]
    [InlineData("error", OverflowPolicy.Error)]
    public void SpellingsAreAccepted(string? text, OverflowPolicy expected)
    {
        Assert.True(ContextOverflow.TryParse(text, out var p));
        Assert.Equal(expected, p);
    }

    [Fact]
    public void UnknownNamesAreRejected() => Assert.False(ContextOverflow.TryParse("shrug", out _));

    [Fact]
    public void RequestFieldsAreRead()
    {
        var g = OpenAiMapping.ReadGeneration(JsonNode.Parse("""{"context_overflow":"truncateMiddle","keep_first_tokens":64}""")!, new GenerationOptions());
        Assert.Equal("truncateMiddle", g.ContextOverflow);
        Assert.Equal(64, g.KeepFirstTokens);
        var lm = OpenAiMapping.ReadGeneration(JsonNode.Parse("""{"contextOverflowPolicy":"rollingWindow"}""")!, new GenerationOptions());
        Assert.Equal("rollingWindow", lm.ContextOverflow);
    }

    [Fact]
    public void SettingsValidationRejectsUnknownPolicies()
    {
        var s = new AppSettings();
        s.DefaultGeneration.ContextOverflow = "sometimes";
        s.DefaultGeneration.KeepFirstTokens = -1;
        var f = SettingsValidator.Validate(s).Select(e => e.Field).ToList();
        Assert.Contains("defaultGeneration.contextOverflow", f);
        Assert.Contains("defaultGeneration.keepFirstTokens", f);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class ContextOverflowIntegrationTests
{
    private static async Task<ModelRuntime> Load(int ctx)
    {
        var model = new ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!)!;
        var o = new LoadOptions { Device = "cpu", ContextSize = (uint)ctx, MaxConcurrent = 1, CacheSlots = 1, Threads = 8, SkipFitCheck = true, Vision = false };
        return await ModelRuntime.LoadAsync(model, o, "overflow-test", null, CancellationToken.None);
    }

    // a grammar that can never finish keeps the model writing (it may not stop with end-of-text), whatever it would say
    private const string NeverEnding = "root ::= \"ab \" root";

    private static List<ChatMessage> LongChat(int turns) =>
        Enumerable.Range(0, turns).SelectMany(i => new[]
        {
            new ChatMessage("user", $"Question number {i}: tell me one fact about the number {i}."),
            new ChatMessage("assistant", $"Fact {i}: the number {i} is the number {i}."),
        }).Append(new ChatMessage("user", "Final question: say done.")).ToList();

    [VlmFact]
    public async Task ErrorPolicyRefusesAndNamesTheLimits()
    {
        using var rt = await Load(512);
        var ex = Assert.Throws<ContextLengthException>(() => rt.BuildFittingPrompt(LongChat(60), 64, out _, out _, "off", "error"));
        Assert.Contains("512", ex.Message);
    }

    [VlmFact]
    public async Task DropOldestKeepsTheNewestAndTheSystemPrompt()
    {
        using var rt = await Load(512);
        var msgs = LongChat(60); msgs.Insert(0, new ChatMessage("system", "Be brief."));
        string prompt = rt.BuildFittingPrompt(msgs, 64, out int dropped, out _, "off", "dropOldest");
        Assert.True(dropped > 0);
        Assert.Contains("Be brief.", prompt);
        Assert.Contains("Final question", prompt);
        Assert.DoesNotContain("Question number 0:", prompt);
        Assert.True(rt.CountTokens(prompt) <= 512 - 64);
    }

    [VlmFact]
    public async Task TruncateMiddleKeepsTheFirstQuestionTheEndAndSaysSo()
    {
        using var rt = await Load(512);
        var msgs = LongChat(60); msgs.Insert(0, new ChatMessage("system", "Be brief."));
        string prompt = rt.BuildFittingPrompt(msgs, 64, out int dropped, out _, "off", "truncateMiddle");
        Assert.True(dropped > 0);
        Assert.Contains("Be brief.", prompt);
        Assert.Contains("Question number 0:", prompt);            // the first question stays
        Assert.Contains("Final question", prompt);                // and so does the newest one
        Assert.DoesNotContain("Question number 30:", prompt);     // the middle is gone
        Assert.Contains($"{dropped} earlier message", prompt);
        Assert.True(rt.CountTokens(prompt) <= 512 - 64);
    }

    [VlmFact]
    public async Task StopAtLimitNeverTouchesTheConversationAndEndsWithLength()
    {
        using var rt = await Load(512);
        var msgs = LongChat(6);
        string prompt = rt.BuildFittingPrompt(msgs, 64, out int dropped, out _, "off", "stopAtLimit");
        Assert.Equal(0, dropped);
        var stats = new GenerationStats();
        await rt.CompleteAsync(prompt, new GenerationOptions { Temperature = 0.8f, Seed = 3, MaxTokens = -1, ReasoningEffort = "off", ContextOverflow = "stopAtLimit", Stop = new() }, stats);
        Assert.True(stats.PromptTokens + stats.CompletionTokens <= 512);
    }

    [VlmFact]
    public async Task RollingWindowLetsTheAnswerRunPastTheWindow()
    {
        using var rt = await Load(512);
        string prompt = rt.BuildFittingPrompt(new() { new ChatMessage("user", "Count upwards from 1, one number per line, and never stop.") }, 64, out _, out _, "off", "rollingWindow");
        var stats = new GenerationStats();
        await rt.CompleteAsync(prompt, new GenerationOptions
        {
            Temperature = 0, MaxTokens = 700, ReasoningEffort = "off", ContextOverflow = "rollingWindow", Stop = new(), Grammar = NeverEnding,
        }, stats);

        // 700 tokens do not fit in 512 cells: the oldest part must have been discarded to go on
        Assert.True(stats.ContextShifts >= 1, "the cache should have been shifted at least once");
        Assert.Equal(700, stats.CompletionTokens);
        Assert.Equal("length", stats.FinishReason);

        // the same request without the policy stops when the window is full
        var plain = new GenerationStats();
        await rt.CompleteAsync(prompt, new GenerationOptions { Temperature = 0, MaxTokens = 700, ReasoningEffort = "off", Stop = new(), Grammar = NeverEnding }, plain);
        Assert.True(plain.CompletionTokens < 512);
        Assert.Equal(0, plain.ContextShifts);
    }
}
