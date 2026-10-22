using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class LoraUnitTests
{
    private static string WriteAdapter(string file, string arch = "llama", string generalType = "adapter", string? adapterType = "lora") =>
        GgufBuilder.WriteFile("lora/" + file, LoraBuilder.Build(arch, new[] { ("blk.0.attn_q.weight", 64, 64) }, rank: 2, generalType: generalType, adapterType: adapterType));

    [Fact]
    public void AdapterMetadataIsRead()
    {
        var info = GgufInfo.Read(WriteAdapter("meta.gguf"));
        Assert.True(info.IsAdapter);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal("lora", info.AdapterType);
        Assert.Equal(8f, info.LoraAlpha);
    }

    [Fact]
    public void ARegularModelIsNotAnAdapter() =>
        Assert.False(GgufInfo.Read(GgufBuilder.WriteFile("lora/plain.gguf", GgufBuilder.Build())).IsAdapter);

    [Fact]
    public void AdaptersAreListedSeparatelyFromModels()
    {
        string dir = Path.Combine(AppSettings.Current.ResolvedModelsDirectory, "pub", "lora-repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "base.gguf"), GgufBuilder.Build());
        File.WriteAllBytes(Path.Combine(dir, "style.gguf"), LoraBuilder.Build("llama", new[] { ("blk.0.attn_q.weight", 64, 64) }, rank: 2));

        var lib = new ModelLibrary();
        Assert.DoesNotContain(lib.List(), m => m.FileName == "style.gguf");
        Assert.Contains(lib.List(), m => m.FileName == "base.gguf");
        var adapter = Assert.Single(lib.ListAdapters(), a => a.FileName == "style.gguf");
        Assert.Equal("llama", adapter.Architecture);
        Assert.Equal(adapter.Id, lib.FindAdapter(adapter.Id)!.Id);                       // by id
        Assert.NotNull(lib.FindAdapter("style.gguf"));                                  // by file name
        Assert.NotNull(lib.FindAdapter(adapter.Path));                                  // by path
        Assert.Null(lib.FindAdapter("no-such-adapter"));
    }

    [Fact]
    public void ResolveAcceptsMatchingAdaptersAndRefusesTheRest()
    {
        var modelInfo = new GgufInfo { Architecture = "llama" };
        var ok = ModelRuntime.ResolveLoras(new[] { new LoraSetting { Id = WriteAdapter("ok.gguf"), Scale = 0.5f } }, modelInfo);
        Assert.Equal(0.5f, Assert.Single(ok).Scale);

        var wrongArch = Assert.Throws<ModelLoadException>(() =>
            ModelRuntime.ResolveLoras(new[] { new LoraSetting { Id = WriteAdapter("qwen.gguf", arch: "qwen2") } }, modelInfo));
        Assert.Contains("architecture", wrongArch.Message);

        var notAdapter = Assert.Throws<ModelLoadException>(() =>
            ModelRuntime.ResolveLoras(new[] { new LoraSetting { Id = GgufBuilder.WriteFile("lora/notlora.gguf", GgufBuilder.Build()) } }, modelInfo));
        Assert.True(notAdapter.Kind is "missing" or "failed");

        var missing = Assert.Throws<ModelLoadException>(() => ModelRuntime.ResolveLoras(new[] { new LoraSetting { Id = "nope.gguf" } }, modelInfo));
        Assert.Equal("missing", missing.Kind);

        Assert.Empty(ModelRuntime.ResolveLoras(new[] { new LoraSetting { Id = "  " } }, modelInfo));      // blank entries are ignored
    }

    [Fact]
    public void ValidationChecksStrengthAndName()
    {
        var s = new AppSettings();
        s.DefaultLoad.Loras.Add(new LoraSetting { Id = "", Scale = 50 });
        var fields = SettingsValidator.Validate(s).Select(e => e.Field).ToList();
        Assert.Contains("defaultLoad.loras[0].id", fields);
        Assert.Contains("defaultLoad.loras[0].scale", fields);
    }

    [Fact]
    public void ClonePreservesAdapters()
    {
        var o = new LoadOptions { Loras = { new LoraSetting { Id = "a", Scale = 0.3f } } };
        var c = o.Clone();
        c.Loras[0].Scale = 2; c.Loras.Add(new LoraSetting());
        Assert.Equal(0.3f, o.Loras[0].Scale);
        Assert.Single(o.Loras);
    }

    [Fact]
    public void TheMemoryEstimateCountsAdapterFiles()
    {
        string adapter = WriteAdapter("est.gguf");
        string path = GgufBuilder.WriteFile("lora/base-est.gguf", GgufBuilder.Build(blocks: 2));
        var model = new LocalModel { Id = "lora/base-est.gguf", Path = path, FileName = "base-est.gguf", Architecture = "llama", SizeBytes = new FileInfo(path).Length };
        var hw = new HardwareInfo("cpu", 8, 64L << 30, Array.Empty<GpuInfo>());

        var without = FitEstimator.Estimate(model, new LoadOptions(), hw, gpuBackend: false);
        var with = FitEstimator.Estimate(model, new LoadOptions { Loras = { new LoraSetting { Id = adapter } } }, hw, gpuBackend: false);

        Assert.Equal(new FileInfo(adapter).Length, with.AdapterBytes);
        Assert.Equal(with.AdapterBytes, with.CpuBytes - without.CpuBytes);
        Assert.Contains("LoRA adapters", with.Describe());
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class LoraIntegrationTests
{
    private static string VlmPath => Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!;

    /// <summary>An adapter for the first layers' query projections of SmolVLM-256M (576 -> 576), with loud random weights.</summary>
    private static string Adapter(string name, string arch = "llama", float magnitude = 0.5f)
    {
        var targets = Enumerable.Range(0, 6).Select(i => ($"blk.{i}.attn_q.weight", 576, 576));
        return GgufBuilder.WriteFile("lora/" + name, LoraBuilder.Build(arch, targets, rank: 4, alpha: 8f, magnitude: magnitude));
    }

    private static async Task<ModelRuntime> Load(params LoraSetting[] loras)
    {
        var model = new ModelLibrary().Find(VlmPath)!;
        var o = new LoadOptions { Device = "cpu", ContextSize = 1024, MaxConcurrent = 1, CacheSlots = 2, Threads = 8, SkipFitCheck = true, Vision = false };
        o.Loras.AddRange(loras);
        return await ModelRuntime.LoadAsync(model, o, "lora-test", null, CancellationToken.None);
    }

    private static async Task<(string Text, GenerationStats Stats)> Say(ModelRuntime rt)
    {
        var prompt = rt.BuildFittingPrompt(new() { new ChatMessage("user", "Name three colours.") }, 32, out _, out _, "off");
        var stats = new GenerationStats();
        return (await rt.CompleteAsync(prompt, new GenerationOptions { Temperature = 0, MaxTokens = 24, ReasoningEffort = "off" }, stats), stats);
    }

    [VlmFact]
    public async Task AnAdapterChangesTheOutputAndStrengthZeroDoesNot()
    {
        var path = Adapter("loud.gguf");
        string plain;
        using (var rt = await Load()) plain = (await Say(rt)).Text;

        using (var off = await Load(new LoraSetting { Id = path, Scale = 0 }))
            Assert.Equal(plain, (await Say(off)).Text);                         // strength 0 = the plain model

        using var on = await Load(new LoraSetting { Id = path, Scale = 4 });
        Assert.Contains(on.LoadWarnings, w => w.Contains("LoRA adapter loud.gguf applied"));
        Assert.Equal("loud.gguf", Assert.Single(on.ActiveLoras).Id);
        Assert.NotEqual(plain, (await Say(on)).Text);                           // the adapter really is in the computation
    }

    [VlmFact]
    public async Task AdaptersCanBeSwappedWithoutReloadingAndTheCacheIsDropped()
    {
        var path = Adapter("swap.gguf");
        using var rt = await Load();
        var (plain, _) = await Say(rt);
        var (_, warm) = await Say(rt);
        Assert.True(warm.CachedTokens > 0);

        await rt.SetLorasAsync(new[] { new LoraSetting { Id = path, Scale = 4 } });
        var (changed, afterSwap) = await Say(rt);
        Assert.Equal(0, afterSwap.CachedTokens);                                // a cache made without the adapter must not be reused
        Assert.NotEqual(plain, changed);

        await rt.SetLorasAsync(Array.Empty<LoraSetting>());
        var (restored, _) = await Say(rt);
        Assert.Equal(plain, restored);
        Assert.Empty(rt.ActiveLoras);
    }

    [VlmFact]
    public async Task ASavedPromptCacheDoesNotCrossAdapters()
    {
        var path = Adapter("cachekey.gguf");
        using var rt = await Load();
        var plainDir = PromptCacheStore.DirFor(rt.CacheModel);
        await rt.SetLorasAsync(new[] { new LoraSetting { Id = path, Scale = 1 } });
        Assert.NotEqual(plainDir, PromptCacheStore.DirFor(rt.CacheModel));
    }

    [VlmFact]
    public async Task AnAdapterForAnotherArchitectureIsRefusedBeforeTheModelLoads()
    {
        var ex = await Assert.ThrowsAsync<ModelLoadException>(() => Load(new LoraSetting { Id = Adapter("qwen.gguf", arch: "qwen2") }));
        Assert.Contains("architecture", ex.Message);
    }
}
