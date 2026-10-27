using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

/// <summary>Several models on several GPUs: what each loaded model occupies, the overlap warning and the per-GPU budget.
/// The tests use GPU indices (90, 91) that no real adapter has, so models loaded by other tests do not interfere.</summary>
[Trait("Category", "Unit")]
public class GpuLedgerTests : IDisposable
{
    private const long Gb = 1L << 30;
    private readonly List<object> _owners = new();
    private object Owner(string model, params (int gpu, long bytes)[] use)
    {
        var o = new object(); _owners.Add(o);
        GpuLedger.Register(o, model, use.ToDictionary(u => u.gpu, u => u.bytes));
        return o;
    }
    public void Dispose() { foreach (var o in _owners) GpuLedger.Release(o); }

    private static GpuInfo Gpu(int index, long vram = 24 * Gb) => new(index, "Test GPU " + index, "Test", vram, false, "");
    private static HardwareInfo Hw(params GpuInfo[] gpus) => new("cpu", 8, 64 * Gb, gpus);

    private static LocalModel Model(string file)
    {
        string path = GgufBuilder.WriteFile("ledger/" + file, GgufBuilder.Build(blocks: 8));
        return new LocalModel { Id = "ledger/" + file, Path = path, FileName = file, Architecture = "llama", SizeBytes = new FileInfo(path).Length };
    }

    [Fact]
    public void ModelsAreAddedSummedPerGpuAndRemoved()
    {
        var a = Owner("model-a", (90, 5 * Gb), (91, 2 * Gb));
        var b = Owner("model-b", (90, 3 * Gb));
        Assert.Equal(8 * Gb, GpuLedger.Reserved(90));
        Assert.Equal(3 * Gb, GpuLedger.Reserved(90, except: a));                              // what the others hold
        Assert.Equal(new[] { "model-a", "model-b" }, GpuLedger.Users(90).Select(u => u.Model));   // largest first
        Assert.Equal(2 * Gb, GpuLedger.Reserved(91));
        GpuLedger.Release(a);
        Assert.Equal(3 * Gb, GpuLedger.Reserved(90));
        Assert.Equal(0, GpuLedger.Reserved(91));
        GpuLedger.Register(b, "model-b", new Dictionary<int, long>());                      // a model without GPU memory leaves the ledger
        Assert.Equal(0, GpuLedger.Reserved(90));
    }

    [Fact]
    public void WhatOtherModelsOccupyIsNotAvailableAgain()
    {
        var g = Gpu(91, 8 * Gb);
        long free = FitEstimator.GpuBudget(g, false, 64 * Gb);
        Assert.Equal(8 * Gb, free);
        Owner("model-a", (91, 7 * Gb));
        Assert.Equal(1 * Gb, FitEstimator.GpuBudget(g, false, 64 * Gb));
        Owner("model-b", (91, 3 * Gb));
        Assert.Equal(0, FitEstimator.GpuBudget(g, false, 64 * Gb));                         // never negative
    }

    [Fact]
    public void AutomaticPlacementPrefersTheGpuThatIsNotTaken()
    {
        var hw = Hw(Gpu(90), Gpu(91));
        var o = new LoadOptions { Device = "gpu" };
        Assert.Equal(new[] { 90, 91 }, FitEstimator.SelectDevices(o, hw, true).Select(g => g.Index));      // nothing loaded: hardware order
        Owner("model-a", (90, 20 * Gb));
        Assert.Equal(new[] { 91, 90 }, FitEstimator.SelectDevices(o, hw, true).Select(g => g.Index));      // 90 is mostly taken
    }

    [Fact]
    public void AnExplicitGpuChoiceIsKeptEvenWhenAnotherModelIsThere()
    {
        var hw = Hw(Gpu(90), Gpu(91));
        Owner("model-a", (90, 20 * Gb));
        var devices = FitEstimator.SelectDevices(new LoadOptions { Device = "gpu", GpuDevices = new List<int> { 90 } }, hw, true);
        Assert.Equal(new[] { 90 }, devices.Select(g => g.Index));
    }

    [Fact]
    public void SharingAGpuIsWarnedAboutAndTheOtherGpuStaysClean()
    {
        var hw = Hw(Gpu(90), Gpu(91));
        var model = Model("overlap-small.gguf");
        Owner("model-a", (90, 2 * Gb));

        var shared = FitEstimator.Estimate(model, new LoadOptions { Device = "gpu", GpuDevices = new List<int> { 90 }, ContextSize = 512 }, hw, gpuBackend: true);
        var d90 = shared.Devices.Single(d => d.GpuIndex == 90);
        Assert.Equal(2 * Gb, d90.ReservedByOthers);
        Assert.Equal(new[] { "model-a" }, d90.SharedWith);
        Assert.Contains(shared.Warnings, w => w.Contains("also used by model-a"));
        Assert.Equal("fits", shared.Verdict);

        var free = FitEstimator.Estimate(model, new LoadOptions { Device = "gpu", GpuDevices = new List<int> { 91 }, ContextSize = 512 }, hw, gpuBackend: true);
        Assert.DoesNotContain(free.Warnings, w => w.Contains("also used by"));
        Assert.Empty(free.Devices.Single(d => d.GpuIndex == 91).SharedWith);
    }

    [Fact]
    public void AutomaticPlacementMovesTheNextModelToTheEmptyGpu()
    {
        var hw = Hw(Gpu(90), Gpu(91));
        var model = Model("overlap-auto.gguf");
        Owner("model-a", (90, 23 * Gb));
        var e = FitEstimator.Estimate(model, new LoadOptions { Device = "gpu", ContextSize = 512 }, hw, gpuBackend: true);
        Assert.Equal(91, e.Devices.First(d => d.IsGpu).GpuIndex);
        Assert.True(e.Devices.Single(d => d.GpuIndex == 91).NeedBytes > 0);
        Assert.Equal(0, e.Devices.Single(d => d.GpuIndex == 90).NeedBytes);                  // 90 is not touched
        Assert.DoesNotContain(e.Warnings, w => w.Contains("also used by"));
    }

    [Fact]
    public void ARefusalNamesTheModelThatTakesTheMemory()
    {
        var hw = Hw(Gpu(90, 1L << 20));                                                      // a 1 MB "GPU"
        var model = Model("overlap-refused.gguf");
        Owner("model-a", (90, 1L << 20));
        var e = FitEstimator.Estimate(model, new LoadOptions { Device = "gpu", GpuDevices = new List<int> { 90 }, ContextSize = 512 }, hw, gpuBackend: true);
        Assert.Equal("wont-fit", e.Verdict);
        string msg = FitEstimator.RefusalMessage(model, e);
        Assert.Contains("taken by model-a", msg);
        Assert.Contains("choose another GPU", msg);
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class GpuUseApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public GpuUseApiTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task EveryGpuIsListedWithWhatIsReservedOnIt()
    {
        var owner = new object();
        var hw = Hardware.Detect();
        try
        {
            if (hw.Gpus.Count > 0) GpuLedger.Register(owner, "api-model", new Dictionary<int, long> { [hw.Gpus[0].Index] = 12345 });
            var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/hardware/gpu-use"))!;
            var gpus = j["gpus"]!.AsArray();
            Assert.Equal(hw.Gpus.Count, gpus.Count);
            if (hw.Gpus.Count > 0)
            {
                Assert.True((long)gpus[0]!["reservedBytes"]! >= 12345);
                Assert.Contains(gpus[0]!["models"]!.AsArray(), m => (string)m!["model"]! == "api-model");
            }
        }
        finally { GpuLedger.Release(owner); }
    }
}
