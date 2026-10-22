using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class InstanceUnitTests
{
    [Theory]
    [InlineData("qwen", "qwen")]
    [InlineData("qwen:2", "qwen")]
    [InlineData("qwen:12", "qwen")]
    [InlineData("llama3:8b", "llama3:8b")]       // a size tag is part of the name, not an instance number
    [InlineData("a:b:3", "a:b")]
    [InlineData("qwen:", "qwen:")]
    public void PoolNameStripsOnlyInstanceNumbers(string id, string pool) => Assert.Equal(pool, ModelManager.PoolName(id));

    private static LocalModel Model(string file)
    {
        string path = GgufBuilder.WriteFile("instances/" + file, GgufBuilder.Build(blocks: 4));
        return new LocalModel { Id = "inst/" + file, Path = path, FileName = file, Architecture = "llama", SizeBytes = new FileInfo(path).Length };
    }

    [Fact]
    public void WeightsAreSharedOnlyWhenTheirPlacementIsTheSame()
    {
        var m = Model("key.gguf");
        string a = ModelRuntime.WeightsKey(m, new LoadOptions(), 0);
        Assert.Equal(a, ModelRuntime.WeightsKey(m, new LoadOptions { ContextSize = 16384, MaxConcurrent = 8, Threads = 3 }, 0));   // context-level settings do not matter
        Assert.NotEqual(a, ModelRuntime.WeightsKey(m, new LoadOptions(), 20));                                                      // other GPU offload
        Assert.NotEqual(a, ModelRuntime.WeightsKey(m, new LoadOptions { UseMmap = false }, 0));
        Assert.NotEqual(a, ModelRuntime.WeightsKey(m, new LoadOptions { MoeCpuLayers = -1 }, 0));
        Assert.NotEqual(a, ModelRuntime.WeightsKey(m, new LoadOptions { MoeExpertCount = 2 }, 0));
        Assert.NotEqual(a, ModelRuntime.WeightsKey(m, new LoadOptions { MainGpu = 1 }, 0));
        Assert.NotEqual(a, ModelRuntime.WeightsKey(Model("key2.gguf"), new LoadOptions(), 0));
    }

    [Fact]
    public void ASharedLoadCountsOnlyContextAndCache()
    {
        var m = Model("est-shared.gguf");
        var hw = new HardwareInfo("cpu", 8, 64L << 30, Array.Empty<GpuInfo>());
        var first = FitEstimator.Estimate(m, new LoadOptions { ContextSize = 2048 }, hw, gpuBackend: false);
        var second = FitEstimator.Estimate(m, new LoadOptions { ContextSize = 2048 }, hw, gpuBackend: false, weightsAlreadyLoaded: true);

        Assert.True(first.WeightsBytes > 0);
        Assert.Equal(0, second.WeightsBytes);
        Assert.Equal(first.WeightsBytes, second.SharedWeightsBytes);
        Assert.Equal(first.CpuBytes - first.WeightsBytes, second.CpuBytes);          // what is left is cache + compute buffers
        Assert.Contains("shared with a loaded instance", second.Describe());
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class InstanceIntegrationTests
{
    private static string VlmPath => Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!;

    private static LoadOptions Opts(uint ctx, bool mmap = true) =>
        new() { Device = "cpu", ContextSize = ctx, MaxConcurrent = 2, CacheSlots = 2, Threads = 4, SkipFitCheck = true, Vision = false, UseMmap = mmap };

    private static async Task<string> Say(ModelRuntime rt, int max = 12)
    {
        var prompt = rt.BuildFittingPrompt(new() { new ChatMessage("user", "Name three colours.") }, 32, out _, out _, "off");
        return await rt.CompleteAsync(prompt, new GenerationOptions { Temperature = 0, MaxTokens = max, ReasoningEffort = "off" }, new GenerationStats());
    }

    [VlmFact]
    public async Task ASecondInstanceSharesTheWeightsAndHasItsOwnContext()
    {
        using var mgr = new ModelManager(new ModelLibrary());
        var one = await mgr.LoadAsync(VlmPath, Opts(1024));
        var two = await mgr.LoadAsync(VlmPath, Opts(2048), default, allowDuplicate: true);

        Assert.NotSame(one, two);
        Assert.Equal(one.Identifier + ":2", two.Identifier);
        Assert.Equal(1024u, one.ContextSize);
        Assert.Equal(2048u, two.ContextSize);
        Assert.Equal(2, one.WeightSharers);
        Assert.Equal(2, two.WeightSharers);
        Assert.True(two.Estimate!.SharedWeightsBytes > 0);

        // both answer, in parallel, the same thing (same weights, greedy)
        var answers = await Task.WhenAll(Say(one), Say(two));
        Assert.Equal(answers[0], answers[1]);
        Assert.False(string.IsNullOrWhiteSpace(answers[0]));

        // asking for the same model again without allowDuplicate returns the existing instance
        Assert.Same(one, await mgr.LoadAsync(VlmPath, Opts(1024)));
    }

    [VlmFact]
    public async Task UnloadingOneInstanceKeepsTheOtherWorking()
    {
        using var mgr = new ModelManager(new ModelLibrary());
        var one = await mgr.LoadAsync(VlmPath, Opts(1024));
        var two = await mgr.LoadAsync(VlmPath, Opts(1024), default, allowDuplicate: true);
        string before = await Say(two);

        Assert.True(mgr.Unload(one.Identifier));
        Assert.Equal(1, two.WeightSharers);
        Assert.Equal(before, await Say(two));                 // the weights stayed in memory

        Assert.True(mgr.Unload(two.Identifier));
        var again = await mgr.LoadAsync(VlmPath, Opts(1024)); // a fresh load after the last user went away
        Assert.Equal(1, again.WeightSharers);
        Assert.Equal(before, await Say(again));
    }

    [VlmFact]
    public async Task DifferentWeightPlacementMeansNoSharing()
    {
        using var mgr = new ModelManager(new ModelLibrary());
        var one = await mgr.LoadAsync(VlmPath, Opts(1024, mmap: true));
        var two = await mgr.LoadAsync(VlmPath, Opts(1024, mmap: false), default, allowDuplicate: true);
        Assert.Equal(1, one.WeightSharers);
        Assert.Equal(1, two.WeightSharers);
        Assert.Equal(0, two.Estimate!.SharedWeightsBytes);
    }

    [VlmFact]
    public async Task RequestsForTheModelNameGoToTheLeastBusyInstance()
    {
        using var mgr = new ModelManager(new ModelLibrary());
        var one = await mgr.LoadAsync(VlmPath, Opts(2048));
        var two = await mgr.LoadAsync(VlmPath, Opts(2048), default, allowDuplicate: true);
        string pool = ModelManager.PoolName(one.Identifier);

        Assert.Equal(2, mgr.Instances(pool).Count);                          // the pool name addresses both
        Assert.Single(mgr.Instances(two.Identifier));                        // "name:2" addresses only that one
        Assert.Same(two, mgr.Instances(two.Identifier)[0]);

        // keep the first instance busy, then ask for the pool: the idle one is chosen
        var busy = Task.Run(() => Say(one, max: 200));
        for (int i = 0; i < 200 && one.ActiveRequests + one.QueuedRequests == 0; i++) await Task.Delay(10);
        Assert.True(one.ActiveRequests + one.QueuedRequests > 0, "the first instance never became busy");
        Assert.Same(two, await mgr.ResolveAsync(pool));
        await busy;

        // an explicit "name:2" is honoured even when it is the busier one
        Assert.Same(two, await mgr.ResolveAsync(two.Identifier));
    }

    [VlmFact]
    public async Task AnExplicitIdentifierThatIsTakenIsRefusedForDuplicates()
    {
        using var mgr = new ModelManager(new ModelLibrary());
        var o = Opts(1024); o.Identifier = "mine";
        await mgr.LoadAsync(VlmPath, o);
        var again = Opts(1024); again.Identifier = "mine";
        await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.LoadAsync(VlmPath, again, default, allowDuplicate: true));
    }
}
