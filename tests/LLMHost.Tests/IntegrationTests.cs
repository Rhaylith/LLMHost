using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

/// <summary>
/// A fact that only runs when LLMHOST_TEST_MODEL points at a small GGUF chat model (CI downloads Qwen2.5-0.5B).
/// Without it the test is reported as skipped.
/// </summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        string? p = Environment.GetEnvironmentVariable("LLMHOST_TEST_MODEL");
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p))
            Skip = "Set LLMHOST_TEST_MODEL to a small .gguf chat model to run the inference integration tests.";
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class ModelIntegrationTests
{
    private static string ModelPath => Environment.GetEnvironmentVariable("LLMHOST_TEST_MODEL")!;

    private static async Task<ModelRuntime> Load(int parallel = 2)
    {
        var library = new ModelLibrary();
        var model = library.Find(ModelPath)!;
        var options = new LoadOptions { Device = "cpu", ContextSize = 2048, MaxConcurrent = parallel, Threads = 4, SkipFitCheck = true };
        return await ModelRuntime.LoadAsync(model, options, "test-model", null, CancellationToken.None);
    }

    // reasoning off: the chat template of thinking models then starts the answer directly
    private static string P(ModelRuntime rt, List<ChatMessage> msgs, int reserve) => rt.BuildFittingPrompt(msgs, reserve, out _, out _, "off");

    private static GenerationOptions Greedy(int max = 12) => new() { Temperature = 0, Seed = 1, MaxTokens = max, TopK = 1, ReasoningEffort = "off" };

    [ModelFact]
    public async Task GeneratesTextAndReportsStats()
    {
        using var rt = await Load();
        var prompt = P(rt, new List<ChatMessage> { new("user", "Say hello in one short sentence.") }, 16);
        var stats = new GenerationStats();
        string text = await rt.CompleteAsync(prompt, Greedy(), stats);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.True(stats.CompletionTokens > 0);
        Assert.True(stats.PromptTokens > 0);
    }

    [ModelFact]
    public async Task SecondRequestReusesThePromptCache()
    {
        using var rt = await Load();
        var msgs = new List<ChatMessage> { new("system", "You are terse and always answer with one word."), new("user", "Name a colour.") };
        var p1 = P(rt, msgs, 16);
        var s1 = new GenerationStats();
        await rt.CompleteAsync(p1, Greedy(4), s1);
        var s2 = new GenerationStats();
        await rt.CompleteAsync(p1, Greedy(4), s2);
        Assert.True(s2.CachedTokens > 0, "an identical prompt must be served from the slot's KV cache");
    }

    [ModelFact]
    public async Task ConcurrentRequestsAreBatchedAndEachCompletes()
    {
        using var rt = await Load(parallel: 3);
        async Task<(string, GenerationStats)> One(string q)
        {
            var p = P(rt, new List<ChatMessage> { new("user", q) }, 16);
            var st = new GenerationStats();
            return (await rt.CompleteAsync(p, Greedy(10), st), st);
        }
        var results = await Task.WhenAll(One("Name a fruit."), One("Name an animal."), One("Name a city."));
        Assert.All(results, r => { Assert.False(string.IsNullOrWhiteSpace(r.Item1)); Assert.True(r.Item2.CompletionTokens > 0); });
        Assert.Equal(0, rt.ActiveRequests);
    }

    [ModelFact]
    public async Task StopStringsAndMaxTokensAreHonoured()
    {
        using var rt = await Load();
        var prompt = P(rt, new List<ChatMessage> { new("user", "Output exactly these numbers separated by commas and nothing else: 1,2,3,4,5,6,7,8") }, 64);
        var st = new GenerationStats();
        var g = Greedy(80);
        g.Stop.Add("5");
        string text = await rt.CompleteAsync(prompt, g, st);
        Assert.False(text.Contains('5'), $"stop string leaked; text was: [{text}] finish={st.FinishReason} stopseq={st.StopSequence}");
        Assert.Equal("stop", st.FinishReason);

        var st2 = new GenerationStats();
        await rt.CompleteAsync(prompt, Greedy(3), st2);
        Assert.Equal("length", st2.FinishReason);
        Assert.Equal(3, st2.CompletionTokens);
    }

    [ModelFact]
    public async Task LogprobsAreRecorded()
    {
        using var rt = await Load();
        var prompt = P(rt, new List<ChatMessage> { new("user", "Say yes.") }, 16);
        var st = new GenerationStats();
        var g = Greedy(4);
        g.Logprobs = true; g.TopLogprobs = 3;
        await rt.CompleteAsync(prompt, g, st);
        Assert.NotEmpty(st.Logprobs);
        Assert.All(st.Logprobs, l => { Assert.True(l.Logprob <= 0.0001); Assert.True(l.Top.Count <= 3); });
    }
}
