using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class SamplerTests
{
    [Fact]
    public void DefaultOrderIsUsedWhenNoneIsGiven()
    {
        Assert.Equal(ConfigurableSamplingPipeline.StageNames, ConfigurableSamplingPipeline.EffectiveOrder(null));
        Assert.Equal(ConfigurableSamplingPipeline.StageNames, ConfigurableSamplingPipeline.EffectiveOrder(new List<string>()));
    }

    [Fact]
    public void CustomOrderIsKeptAndNormalised()
    {
        var order = ConfigurableSamplingPipeline.EffectiveOrder(new[] { "Temperature", "top-k", "typical_p", "top_k", "bogus", "min_p" });
        Assert.Equal(new[] { "temperature", "top_k", "typ_p", "min_p" }, order);
    }

    [Fact]
    public void UnknownStagesAreReportedByValidation()
    {
        var s = new AppSettings();
        s.DefaultGeneration.SamplerOrder = new List<string> { "top_k", "tfs" };
        s.DefaultGeneration.Mirostat = 3;
        s.DefaultGeneration.XtcProbability = 2;
        var fields = SettingsValidator.Validate(s).Select(e => e.Field).ToList();
        Assert.Contains("defaultGeneration.samplerOrder", fields);
        Assert.Contains("defaultGeneration.mirostat", fields);
        Assert.Contains("defaultGeneration.xtcProbability", fields);
    }

    [Fact]
    public void RequestFieldsUseLlamaServerNames()
    {
        var body = JsonNode.Parse("""
            {"mirostat":2,"mirostat_tau":4.5,"mirostat_eta":0.2,"dry_multiplier":0.8,"dry_base":2,"dry_allowed_length":3,
             "dry_penalty_last_n":256,"dry_sequence_breakers":["\n","#"],"xtc_probability":0.5,"xtc_threshold":0.2,
             "top_n_sigma":1.5,"samplers":["top_k","temperature"]}
            """)!;
        var g = OpenAiMapping.ReadGeneration(body, new GenerationOptions());
        Assert.Equal(2, g.Mirostat);
        Assert.Equal(4.5f, g.MirostatTau);
        Assert.Equal(0.8f, g.DryMultiplier);
        Assert.Equal(3, g.DryAllowedLength);
        Assert.Equal(256, g.DryPenaltyLastN);
        Assert.Equal(new[] { "\n", "#" }, g.DrySequenceBreakers);
        Assert.Equal(0.5f, g.XtcProbability);
        Assert.Equal(1.5f, g.TopNSigma);
        Assert.Equal(new[] { "top_k", "temperature" }, g.SamplerOrder);
    }

    [Fact]
    public void NewSamplersAreOffByDefault()
    {
        var g = new GenerationOptions();
        Assert.Equal(0, g.Mirostat);
        Assert.Equal(0f, g.DryMultiplier);
        Assert.Equal(0f, g.XtcProbability);
        Assert.Equal(0f, g.TopNSigma);
        Assert.Null(g.SamplerOrder);
    }
}

[Trait("Category", "Integration")]
public class SamplerIntegrationTests
{
    private static async Task<ModelRuntime> Load()
    {
        var model = new LLMHostProject.Models.ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_MODEL")!)!;
        var o = new LoadOptions { Device = "cpu", ContextSize = 2048, MaxConcurrent = 1, Threads = 8, SkipFitCheck = true };
        return await ModelRuntime.LoadAsync(model, o, "sampler-test", null, CancellationToken.None);
    }

    private static List<ChatMessage> Prompt() => new() { new ChatMessage("user", "Write the word 'banana' five times separated by spaces.") };

    private static async Task<string> Run(ModelRuntime rt, GenerationOptions g)
    {
        var prompt = rt.BuildFittingPrompt(Prompt(), 24, out _, out _, "off");
        return await rt.CompleteAsync(prompt, g, new GenerationStats(), CancellationToken.None);
    }

    private static GenerationOptions Base() => new() { Temperature = 0.8f, Seed = 7, MaxTokens = 16, ReasoningEffort = "off" };

    [ModelFact]
    public async Task EverySamplerProducesTextAndSameSeedIsRepeatable()
    {
        using var rt = await Load();
        var variants = new Dictionary<string, Action<GenerationOptions>>
        {
            ["default"] = _ => { },
            ["mirostat1"] = g => g.Mirostat = 1,
            ["mirostat2"] = g => g.Mirostat = 2,
            ["dry"] = g => g.DryMultiplier = 0.8f,
            ["xtc"] = g => { g.XtcProbability = 0.5f; g.XtcThreshold = 0.1f; },
            ["top_n_sigma"] = g => g.TopNSigma = 1.0f,
            ["order"] = g => g.SamplerOrder = new List<string> { "temperature", "top_k", "min_p" },
            ["greedy"] = g => g.Temperature = 0,
        };
        foreach (var (name, apply) in variants)
        {
            var g1 = Base(); apply(g1);
            var g2 = Base(); apply(g2);
            string a = await Run(rt, g1), b = await Run(rt, g2);
            Assert.False(string.IsNullOrWhiteSpace(a), name + " produced no text");
            Assert.Equal(a, b);                                   // same seed => same output
        }
    }
}
