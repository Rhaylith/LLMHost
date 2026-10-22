using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class OnboardingRecommendationTests
{
    private const long Gb = 1L << 30;

    [Theory]
    [InlineData("8B", 8)]
    [InlineData("0.5B", 0.5)]
    [InlineData("137M", 0.137)]
    [InlineData("568M", 0.568)]
    [InlineData("30B", 30)]
    [InlineData("", 0)]
    [InlineData("big", 0)]
    public void ParameterLabelsBecomeBillions(string label, double expected) => Assert.Equal(expected, Onboarding.ParamsBillions(label), 3);

    [Fact]
    public void TheEstimateGrowsWithTheModelAndCountsTheContext()
    {
        var small = Catalog.Find("qwen2.5-0.5b")!; var big = Catalog.Find("llama3.3-70b")!;
        Assert.True(Onboarding.EstimateBytes(small) < Onboarding.EstimateBytes(big));
        Assert.InRange(Onboarding.EstimateBytes(Catalog.Find("llama3.1-8b")!) / (double)Gb, 4.5, 6);          // 8B at Q4: about 5 GB with context
        Assert.True(Onboarding.EstimateBytes(small) > 700L << 20);
    }

    [Fact]
    public void ABigMachineGetsASmallAMediumAndAVisionModelThatAllFit()
    {
        long budget = 24 * Gb;
        var r = Onboarding.Recommend(budget, Catalog.Entries);
        Assert.Equal(new[] { "small", "medium", "vision" }, r.Select(x => x.Slot));
        Assert.All(r, x => Assert.True(x.EstimatedBytes <= budget));
        Assert.True(r[1].EstimatedBytes <= budget * 0.7);
        Assert.True(Onboarding.ParamsBillions(r[1].Entry.Params) > Onboarding.ParamsBillions(r[0].Entry.Params));
        Assert.True(r[2].Entry.IsVision);
        Assert.Equal(3, r.Select(x => x.Entry.Key).Distinct().Count());
    }

    [Fact]
    public void ASmallMachineGetsOnlyWhatFits()
    {
        long budget = 3 * Gb;
        var r = Onboarding.Recommend(budget, Catalog.Entries);
        Assert.NotEmpty(r);
        Assert.All(r, x => Assert.True(x.EstimatedBytes <= budget, $"{x.Entry.Name} needs {x.EstimatedBytes}"));
        Assert.DoesNotContain(r, x => Onboarding.ParamsBillions(x.Entry.Params) > 4);
    }

    [Fact]
    public void ATinyMachineGetsNothingAndEmbeddingModelsAreNeverSuggested()
    {
        Assert.Empty(Onboarding.Recommend(256L << 20, Catalog.Entries));
        var all = Onboarding.Recommend(64 * Gb, Catalog.Entries);
        Assert.DoesNotContain(all, x => x.Entry.Tags.Contains("embeddings") || x.Entry.Tags.Contains("rerank"));
    }

    [Fact]
    public void TheBudgetIsTheBiggerOfTheGraphicsMemoryAndMostOfTheFreeRam()
    {
        var gpu = new GpuInfo(0, "Big GPU", "Test", 24 * Gb, false, "");
        var integrated = new GpuInfo(1, "iGPU", "Test", 0, true, "", 8 * Gb);
        Assert.Equal(24 * Gb, Onboarding.Budget(new HardwareInfo("cpu", 8, 64 * Gb, new[] { gpu }), 20 * Gb));
        Assert.Equal((long)(20 * Gb * 0.6), Onboarding.Budget(new HardwareInfo("cpu", 8, 64 * Gb, Array.Empty<GpuInfo>()), 20 * Gb));
        Assert.Equal((long)(20 * Gb * 0.6), Onboarding.Budget(new HardwareInfo("cpu", 8, 64 * Gb, new[] { integrated }), 20 * Gb));   // an integrated GPU adds nothing beyond RAM
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class OnboardingApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public OnboardingApiTests(ServerFixture f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task TheWizardGetsHardwareFolderAndRecommendations()
    {
        var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/onboarding"))!;
        Assert.True((int)j["hardware"]!["logicalCores"]! >= 1);
        Assert.True((long)j["hardware"]!["ramTotalBytes"]! > 0);
        Assert.NotNull(j["hardware"]!["cpuFeatures"]!["avx2"]);
        Assert.NotNull(j["hardware"]!["vulkan"]);
        Assert.True((long)j["folder"]!["freeBytes"]! > 0);
        Assert.False(string.IsNullOrEmpty((string)j["folder"]!["path"]!));
        foreach (var r in j["recommendations"]!.AsArray())
        {
            Assert.Contains((string)r!["slot"]!, new[] { "small", "medium", "vision" });
            Assert.True((long)r["estimatedBytes"]! <= (long)j["hardware"]!["budgetBytes"]!);
            Assert.Contains("/", (string)r["repo"]!);
        }
    }

    [Fact]
    public async Task CompletingStoresTheModeAndStopsAskingAndTheGuideCanBeRepeated()
    {
        var oldDone = _f.Host.Settings.OnboardingDone; var oldMode = _f.Host.Settings.UiMode;
        try
        {
            var r = JsonNode.Parse(await (await _f.Http.PostAsync("/api/onboarding/complete", Body("""{"mode":"user"}"""))).Content.ReadAsStringAsync())!;
            Assert.True((bool)r["done"]!);
            Assert.Equal("user", _f.Host.Settings.UiMode);
            var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/onboarding"))!;
            Assert.True((bool)j["done"]!);
            Assert.False((bool)j["needed"]!);

            await _f.Http.PostAsync("/api/onboarding/complete", Body("""{"mode":"nonsense"}"""));
            Assert.Equal("user", _f.Host.Settings.UiMode);                                  // an unknown mode changes nothing

            await _f.Http.PostAsync("/api/onboarding/reset", Body("{}"));
            Assert.False(_f.Host.Settings.OnboardingDone);
        }
        finally { _f.Host.Settings.OnboardingDone = oldDone; _f.Host.Settings.UiMode = oldMode; }
    }

    [Fact]
    public async Task ItIsOnlyNeededOnAnEmptyInstallation()
    {
        GgufBuilder.WriteFile("library/onbpub/onbmodel/onb-Q4_0.gguf", GgufBuilder.Build());
        var old = _f.Host.Settings.OnboardingDone;
        try
        {
            _f.Host.Settings.OnboardingDone = false;
            Assert.False((bool)JsonNode.Parse(await _f.Http.GetStringAsync("/api/onboarding"))!["needed"]!);          // there is a model: not a first run
        }
        finally { _f.Host.Settings.OnboardingDone = old; }
    }

    [Fact]
    public async Task ThePageHasTheWizard()
    {
        string html = await _f.Http.GetStringAsync("/");
        Assert.Contains("id=\"wizard\"", html);
        string js = await _f.Http.GetStringAsync("/ui/onboarding.js");
        foreach (var step in new[] { "welcome", "hardware", "folder", "models", "token", "done" }) Assert.Contains($"'{step}'", js);
        Assert.Contains("wizFinish(false)", js);                                           // "skip" is on every step
    }
}
