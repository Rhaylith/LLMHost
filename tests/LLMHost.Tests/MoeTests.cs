using System.Text.RegularExpressions;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class MoeUnitTests
{
    [Fact]
    public void PatternForTheFirstLayersMatchesOnlyThoseExpertTensors()
    {
        var rx = new Regex(TensorOverrides.MoePattern(2));
        Assert.Matches(rx, "blk.0.ffn_up_exps.weight");
        Assert.Matches(rx, "blk.1.ffn_down_exps.weight");
        Assert.Matches(rx, "blk.1.ffn_gate_up_exps.weight");
        Assert.DoesNotMatch(rx, "blk.2.ffn_up_exps.weight");
        Assert.DoesNotMatch(rx, "blk.10.ffn_up_exps.weight");        // not layer 1 + "0"
        Assert.DoesNotMatch(rx, "blk.0.ffn_up.weight");               // a dense feed-forward tensor is not an expert
        Assert.DoesNotMatch(rx, "blk.0.attn_q.weight");
    }

    [Fact]
    public void PatternForAllLayersMatchesEveryExpertTensor()
    {
        var rx = new Regex(TensorOverrides.MoePattern(-1));
        Assert.Matches(rx, "blk.0.ffn_up_exps.weight");
        Assert.Matches(rx, "blk.47.ffn_down_exps.weight");
        Assert.DoesNotMatch(rx, "blk.47.ffn_down_shexp.weight");      // shared experts stay with the attention
    }

    [Fact]
    public void EffectiveRulesCombineTheShortcutAndExplicitOnes()
    {
        var o = new LoadOptions { MoeCpuLayers = -1, TensorOverrides = { new TensorOverrideSetting { Pattern = "output", Buffer = "CPU" }, new TensorOverrideSetting { Pattern = " ", Buffer = "CPU" } } };
        var rules = TensorOverrides.Effective(o);
        Assert.Equal(2, rules.Count);                                  // blank pattern dropped
        Assert.Equal(2, TensorOverrides.HostRules(o).Count);
        Assert.Empty(TensorOverrides.Effective(new LoadOptions()));
    }

    [Fact]
    public void NonHostBuffersAreNotCountedAsSystemMemory()
    {
        var o = new LoadOptions { TensorOverrides = { new TensorOverrideSetting { Pattern = "x", Buffer = "Vulkan1" } } };
        Assert.Empty(TensorOverrides.HostRules(o));
    }

    [Fact]
    public void CheckReportsBadValues()
    {
        Assert.Empty(TensorOverrides.Check(new LoadOptions()));
        Assert.NotEmpty(TensorOverrides.Check(new LoadOptions { MoeCpuLayers = -5 }));
        Assert.NotEmpty(TensorOverrides.Check(new LoadOptions { MoeExpertCount = -1 }));
        Assert.Contains("regular expression", TensorOverrides.Check(new LoadOptions { TensorOverrides = { new TensorOverrideSetting { Pattern = "(", Buffer = "CPU" } } }).Single());
        Assert.Contains("no buffer", TensorOverrides.Check(new LoadOptions { TensorOverrides = { new TensorOverrideSetting { Pattern = "a", Buffer = "" } } }).Single());
        var s = new AppSettings(); s.DefaultLoad.MoeCpuLayers = -9;
        Assert.Contains(SettingsValidator.Validate(s), e => e.Field == "defaultLoad.tensorOverrides");
    }

    private static (LocalModel Model, HardwareInfo Hw) Moe(string file, int blocks, int? experts)
    {
        string path = GgufBuilder.WriteFile("moe/" + file, GgufBuilder.Build(arch: "llama", blocks: blocks, experts: experts, dataBytesPerTensor: 4096));
        var model = new LocalModel { Id = "moe/" + file, Path = path, FileName = file, Architecture = "llama", SizeBytes = new FileInfo(path).Length };
        var hw = new HardwareInfo("cpu", 8, 64L << 30, new[] { new GpuInfo(0, "Test GPU", "Test", 24L << 30, false, "") });
        return (model, hw);
    }

    [Fact]
    public void KeepingExpertsInSystemMemoryMovesTheirBytesOffTheGpu()
    {
        var (model, hw) = Moe("est.gguf", 4, 8);

        var moeOff = FitEstimator.Estimate(model, new LoadOptions { Device = "gpu", ContextSize = 2048 }, hw, gpuLayersOverride: 999, gpuBackend: true);
        var moeOn = FitEstimator.Estimate(model, new LoadOptions { Device = "gpu", ContextSize = 2048, MoeCpuLayers = -1 }, hw, gpuLayersOverride: 999, gpuBackend: true);

        long experts = 4 * 3 * 4096;                                   // 4 layers x (up, down, gate) x 4096 bytes
        Assert.Equal(experts, moeOff.GpuBytes - moeOn.GpuBytes);
        Assert.Equal(experts, moeOn.CpuBytes - moeOff.CpuBytes);
        Assert.Equal(moeOff.WeightsBytes, moeOn.WeightsBytes);        // the model is the same size, only placed differently

        // only the first two layers
        var two = FitEstimator.Estimate(model, new LoadOptions { Device = "gpu", ContextSize = 2048, MoeCpuLayers = 2 }, hw, gpuLayersOverride: 999, gpuBackend: true);
        Assert.Equal(experts / 2, moeOff.GpuBytes - two.GpuBytes);
    }

    [Fact]
    public void MoeOptionsOnADenseModelAreFlagged()
    {
        var (model, hw) = Moe("dense.gguf", 2, null);
        var e = FitEstimator.Estimate(model, new LoadOptions { MoeCpuLayers = -1 }, hw, gpuBackend: false);
        Assert.Contains(e.Warnings, w => w.Contains("not a mixture-of-experts"));
    }

    [Fact]
    public void CloningCopiesTensorOverrides()
    {
        var o = new LoadOptions { TensorOverrides = { new TensorOverrideSetting { Pattern = "a", Buffer = "CPU" } } };
        var c = o.Clone();
        c.TensorOverrides[0].Pattern = "changed"; c.TensorOverrides.Add(new TensorOverrideSetting());
        Assert.Equal("a", o.TensorOverrides[0].Pattern);
        Assert.Single(o.TensorOverrides);
    }
}

/// <summary>Runs only when LLMHOST_TEST_MOE points at a mixture-of-experts GGUF (for example Qwen3-30B-A3B).</summary>
public sealed class MoeFactAttribute : FactAttribute
{
    public MoeFactAttribute()
    {
        string? p = Environment.GetEnvironmentVariable("LLMHOST_TEST_MOE");
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) Skip = "Set LLMHOST_TEST_MOE to a mixture-of-experts .gguf to run the MoE integration tests.";
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class MoeIntegrationTests
{
    private static string ModelFile => Environment.GetEnvironmentVariable("LLMHOST_TEST_MOE")!;

    private static async Task<string> Say(ModelRuntime rt)
    {
        var prompt = rt.BuildFittingPrompt(new() { new ChatMessage("user", "Say hello.") }, 32, out _, out _, "off");
        return await rt.CompleteAsync(prompt, new GenerationOptions { Temperature = 0, MaxTokens = 8, ReasoningEffort = "off" }, new GenerationStats());
    }

    [MoeFact]
    public async Task FewerExpertsAreAppliedToTheModelMetadata()
    {
        var model = new ModelLibrary().Find(ModelFile)!;
        var o = new LoadOptions { Device = "cpu", ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 8, SkipFitCheck = true, Vision = false, MoeExpertCount = 2 };
        using var rt = await ModelRuntime.LoadAsync(model, o, "moe-experts", null, CancellationToken.None);

        Assert.Contains(rt.LoadWarnings, w => w.Contains("2 of"));
        Assert.Contains(LogBuffer.Tail(4000), l => l.Contains("n_expert_used") && l.Contains("= 2"));
        Assert.False(string.IsNullOrWhiteSpace(await Say(rt)));
    }

    [MoeFact]
    public async Task ExpertsCanStayInSystemMemoryWhileTheRestRunsOnTheGpu()
    {
        if (!Hardware.Detect().Gpus.Any(g => !g.Integrated)) return;               // needs a discrete GPU
        var model = new ModelLibrary().Find(ModelFile)!;
        var logging = AppSettings.Current.Logging; string before = logging.Native;
        logging.Native = "debug";                                                   // the override is only reported at debug level
        var o = new LoadOptions { Device = "gpu", ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 8, SkipFitCheck = true, Vision = false, MoeCpuLayers = -1 };
        ModelRuntime rt;
        try { rt = await ModelRuntime.LoadAsync(model, o, "moe-cpu", null, CancellationToken.None); }
        finally { logging.Native = before; }
        using var _ = rt;

        if (NativeBackend.Active == "cpu") return;                                  // no GPU backend in this build
        Assert.Contains(LogBuffer.Tail(4000), l => l.Contains("buffer type overridden"));
        Assert.False(string.IsNullOrWhiteSpace(await Say(rt)));
    }
}
