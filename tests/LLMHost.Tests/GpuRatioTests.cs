using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class GpuRatioTests
{
    [Theory]
    [InlineData(0.5, 32, 17)]        // 0.5 x 33 offloadable layers = 16.5, rounded away from zero
    [InlineData(0.25, 32, 8)]        // 8.25
    [InlineData(0.75, 32, 25)]       // 24.75
    [InlineData(0.1, 32, 3)]         // 3.3
    [InlineData(0.01, 32, 0)]        // 0.33 rounds to none
    [InlineData(0.99, 32, 999)]      // 32.67 reaches the output layer: everything
    [InlineData(1.0, 32, 999)]
    [InlineData(2.0, 32, 999)]
    [InlineData(0.0, 32, 0)]
    [InlineData(-1.0, 32, 0)]
    [InlineData(0.5, 1, 1)]          // a one-block model has two offloadable layers
    public void RatioBecomesALayerCount(double ratio, long blocks, int expected) =>
        Assert.Equal(expected, LoadOptions.LayersForRatio(ratio, blocks));

    [Fact]
    public void WithoutALayerCountOnlyTheExtremesAreKnown()
    {
        Assert.Equal(999, LoadOptions.LayersForRatio(1, 0));
        Assert.Equal(0, LoadOptions.LayersForRatio(0, 0));
        Assert.Null(LoadOptions.LayersForRatio(0.5, 0));
        Assert.Null(LoadOptions.LayersForRatio(double.NaN, 32));
    }

    [Fact]
    public void AnExactLayerCountBeatsTheRatioWhichBeatsTheDevice()
    {
        Assert.Equal(5, new LoadOptions { GpuLayers = 5, GpuRatio = 0.9f }.RequestedGpuLayers(32));
        Assert.Equal(0, new LoadOptions { GpuLayers = 0, GpuRatio = 0.9f }.RequestedGpuLayers(32));
        Assert.Equal(30, new LoadOptions { GpuRatio = 0.9f, Device = "cpu" }.RequestedGpuLayers(32));    // 29.7
        Assert.Equal(8, new LoadOptions { Device = "0.25" }.RequestedGpuLayers(32));                        // a ratio written as the device
        Assert.Null(new LoadOptions { Device = "auto" }.RequestedGpuLayers(32));
        Assert.Null(new LoadOptions { Device = "cpu" }.RequestedGpuLayers(32));
    }

    [Theory]
    [InlineData("off", "cpu", null)]
    [InlineData("cpu", "cpu", null)]
    [InlineData("max", "gpu", null)]
    [InlineData("ALL", "gpu", null)]
    [InlineData("auto", "auto", null)]
    [InlineData("1", "gpu", null)]
    [InlineData("1.0", "gpu", null)]
    [InlineData("0", "cpu", null)]
    [InlineData("0.5", "gpu", 0.5f)]
    [InlineData("0.25", "gpu", 0.25f)]
    public void TheGpuNotationIsUnderstood(string text, string device, float? ratio)
    {
        var o = new LoadOptions { GpuRatio = 0.9f };           // an earlier ratio is replaced
        o.SetGpu(text);
        Assert.Equal(device, o.Device);
        Assert.Equal(ratio, o.GpuRatio);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("-0.1")]
    [InlineData("most")]
    public void ANonsenseGpuValueIsRefused(string text) => Assert.Throws<ArgumentException>(() => new LoadOptions().SetGpu(text));

    [Fact]
    public void ValidationAcceptsRatiosAndMaxAndRejectsTheRest()
    {
        var ok = new AppSettings();
        ok.DefaultLoad.GpuRatio = 0.4f; ok.DefaultLoad.Device = "max";
        Assert.Empty(SettingsValidator.Validate(ok));
        ok.DefaultLoad.Device = "0.5";
        Assert.Empty(SettingsValidator.Validate(ok));

        var bad = new AppSettings();
        bad.DefaultLoad.GpuRatio = 1.5f; bad.DefaultLoad.Device = "2";
        var fields = SettingsValidator.Validate(bad).Select(e => e.Field).ToList();
        Assert.Contains("defaultLoad.gpuRatio", fields);
        Assert.Contains("defaultLoad.device", fields);
    }

    private static LocalModel Model(int blocks, string file)
    {
        string path = GgufBuilder.WriteFile("ratio/" + file, GgufBuilder.Build(blocks: blocks));
        var info = GgufInfo.Read(path);
        return new LocalModel { Id = "ratio/" + file, Path = path, FileName = file, Architecture = "llama", SizeBytes = new FileInfo(path).Length };
    }

    [Fact]
    public void ThePlannerUsesTheRatioOfTheModelsOwnLayerCount()
    {
        NativeBackend.Initialize();
        if (!NativeBackend.GpuAvailable) return;                          // without a GPU backend nothing is offloaded
        var model = Model(32, "plan32.gguf");
        var info = GgufInfo.Read(model.Path);

        Assert.Equal(17, ModelRuntime.PlanGpuLayers(model, new LoadOptions { GpuRatio = 0.5f }, info));
        Assert.Equal(5, ModelRuntime.PlanGpuLayers(model, new LoadOptions { GpuRatio = 0.5f, GpuLayers = 5 }, info));
        Assert.Equal(999, ModelRuntime.PlanGpuLayers(model, new LoadOptions { Device = "max" }, info));
        Assert.Equal(8, ModelRuntime.PlanGpuLayers(model, new LoadOptions { Device = "0.25" }, info));
        Assert.Equal(0, ModelRuntime.PlanGpuLayers(model, new LoadOptions { Device = "cpu" }, info));
        Assert.Equal(0, ModelRuntime.PlanGpuLayers(model, new LoadOptions { GpuRatio = 0f }, info));
    }

    [Fact]
    public void TheMemoryEstimateOffloadsTheRequestedShare()
    {
        var model = Model(32, "est32.gguf");
        var hw = new HardwareInfo("cpu", 8, 64L << 30, new[] { new GpuInfo(0, "Test GPU", "Test", 24L << 30, false, "") });

        var half = FitEstimator.Estimate(model, new LoadOptions { GpuRatio = 0.5f, ContextSize = 1024 }, hw, gpuBackend: true);
        Assert.Equal(17, half.OffloadedLayers);

        var none = FitEstimator.Estimate(model, new LoadOptions { GpuRatio = 0f, ContextSize = 1024 }, hw, gpuBackend: true);
        Assert.Equal(0, none.OffloadedLayers);
        Assert.Empty(none.Devices.Where(d => d.IsGpu));

        var all = FitEstimator.Estimate(model, new LoadOptions { Device = "max", ContextSize = 1024 }, hw, gpuBackend: true);
        Assert.Equal(32, all.OffloadedLayers);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class GpuRatioIntegrationTests
{
    [VlmFact]
    public async Task ALoadWithARatioOffloadsThatManyLayers()
    {
        NativeBackend.Initialize();
        if (!NativeBackend.GpuAvailable) return;
        var model = new ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!)!;
        int blocks = (int)GgufInfo.Read(model.Path).BlockCount;
        var o = new LoadOptions { Device = "cpu", GpuRatio = 0.5f, ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Vision = false };
        using var rt = await ModelRuntime.LoadAsync(model, o, "ratio-load", null, CancellationToken.None);
        Assert.Equal(LoadOptions.LayersForRatio(0.5, blocks), rt.GpuLayers);
        Assert.True(rt.GpuLayers > 0 && rt.GpuLayers < 999);
    }
}
