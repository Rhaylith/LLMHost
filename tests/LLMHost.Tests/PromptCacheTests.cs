using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class PromptCacheStoreTests
{
    private static LocalModel Model(string id) => new() { Id = id, SizeBytes = 123 };

    [Theory]
    [InlineData("chat-1", true)]
    [InlineData("auto-0", true)]
    [InlineData("a.b_c", true)]
    [InlineData("", false)]
    [InlineData("..\evil", false)]
    [InlineData("../evil", false)]
    [InlineData(".hidden", false)]
    [InlineData("has space", false)]
    public void NamesAreRestricted(string name, bool ok) => Assert.Equal(ok, PromptCacheStore.IsValidName(name));

    [Fact]
    public void InvalidNameThrowsBeforeTouchingDisk() =>
        Assert.Throws<ArgumentException>(() => PromptCacheStore.KvPath(Model("a/b.gguf"), "../x"));

    [Fact]
    public void ModelsGetSeparateFolders()
    {
        Assert.NotEqual(PromptCacheStore.DirFor(Model("a/b.gguf")), PromptCacheStore.DirFor(Model("a/c.gguf")));
        var changed = new LocalModel { Id = "a/b.gguf", SizeBytes = 124 };
        Assert.NotEqual(PromptCacheStore.DirFor(Model("a/b.gguf")), PromptCacheStore.DirFor(changed));
    }

    [Fact]
    public void ListAndDeleteWorkOnMetadata()
    {
        var m = Model("list/" + Guid.NewGuid().ToString("N") + ".gguf");
        string kv = PromptCacheStore.KvPath(m, "one");
        Directory.CreateDirectory(Path.GetDirectoryName(kv)!);
        File.WriteAllBytes(kv, new byte[100]);
        PromptCacheStore.WriteMeta(kv, new SavedCache("one", 42, 100, DateTime.UtcNow, m.Id, "f16", "f16"));

        var list = PromptCacheStore.List(m);
        Assert.Single(list);
        Assert.Equal(42, list[0].Tokens);
        Assert.Equal(100, list[0].Bytes);
        Assert.True(PromptCacheStore.Delete(m, "one"));
        Assert.Empty(PromptCacheStore.List(m));
        Assert.False(PromptCacheStore.Delete(m, "one"));
    }

    [Fact]
    public void DiskLimitRemovesTheOldestFirst()
    {
        var m = Model("limit/" + Guid.NewGuid().ToString("N") + ".gguf");
        var dir = PromptCacheStore.DirFor(m);
        Directory.CreateDirectory(dir);
        foreach (var (name, age) in new[] { ("old", 3), ("mid", 2), ("new", 1) })
        {
            string kv = PromptCacheStore.KvPath(m, name);
            File.WriteAllBytes(kv, new byte[1000]);
            PromptCacheStore.WriteMeta(kv, new SavedCache(name, 1, 1000, DateTime.UtcNow, m.Id, "f16", "f16"));
            File.SetLastWriteTimeUtc(kv, DateTime.UtcNow.AddHours(-age));
        }
        long others = Directory.EnumerateFiles(PromptCacheStore.Root, "*.kv", SearchOption.AllDirectories).Where(f => !f.StartsWith(dir)).Sum(f => new FileInfo(f).Length);

        PromptCacheStore.EnforceLimit(others + 1500);      // room for one 1000-byte file only

        var left = PromptCacheStore.List(m).Select(c => c.Name).ToList();
        Assert.Equal(new[] { "new" }, left);
    }

    [Fact]
    public void SlotCountFollowsCacheSlotsAndConcurrency()
    {
        Assert.Equal(3, new LoadOptions { MaxConcurrent = 1 }.SlotCount);
        Assert.Equal(6, new LoadOptions { MaxConcurrent = 4 }.SlotCount);
        Assert.Equal(8, new LoadOptions { MaxConcurrent = 4, CacheSlots = 8 }.SlotCount);
        Assert.Equal(4, new LoadOptions { MaxConcurrent = 4, CacheSlots = 2 }.SlotCount);   // never fewer than parallel requests
        Assert.Equal(16, new LoadOptions { MaxConcurrent = 16, CacheSlots = 99 }.SlotCount);
    }

    [Fact]
    public void SavedCacheSettingsAreValidated()
    {
        var s = new AppSettings();
        s.PromptCache.MaxDiskMb = -5;
        Assert.Contains(SettingsValidator.Validate(s), e => e.Field == "promptCache.maxDiskMb");
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class PromptCacheIntegrationTests
{
    private static async Task<ModelRuntime> Load(int parallel = 1, int cacheSlots = 3)
    {
        var model = new ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_MODEL")!)!;
        var o = new LoadOptions { Device = "cpu", ContextSize = 2048, MaxConcurrent = parallel, CacheSlots = cacheSlots, Threads = 8, SkipFitCheck = true };
        return await ModelRuntime.LoadAsync(model, o, "cache-test", null, CancellationToken.None);
    }

    private static GenerationOptions G() => new() { Temperature = 0, MaxTokens = 2, ReasoningEffort = "off" };

    private static string Doc(string topic) =>
        string.Join(" ", Enumerable.Range(0, 40).Select(i => $"{topic} fact number {i} is noted here.")) + "\nSummarize in one word:";

    private static async Task<GenerationStats> Run(ModelRuntime rt, string prompt)
    {
        var stats = new GenerationStats();
        await rt.CompleteAsync(prompt, G(), stats);
        return stats;
    }

    [ModelFact]
    public async Task AlternatingConversationsEachKeepTheirCache()
    {
        using var rt = await Load();
        string a = Doc("apples"), b = Doc("bridges");
        var a1 = await Run(rt, a);
        var b1 = await Run(rt, b);
        var a2 = await Run(rt, a + " Again:");
        var b2 = await Run(rt, b + " Again:");

        Assert.Equal(0, a1.CachedTokens);
        Assert.Equal(0, b1.CachedTokens);
        Assert.True(a2.CachedTokens >= a1.PromptTokens - 2, $"A reused only {a2.CachedTokens} of {a1.PromptTokens}");
        Assert.True(b2.CachedTokens >= b1.PromptTokens - 2, $"B reused only {b2.CachedTokens} of {b1.PromptTokens}");
        Assert.True(rt.CacheSlots.Count(s => s.Tokens > 0) >= 2);
    }

    [ModelFact]
    public async Task SavedCacheRestoresAfterTheCacheWasCleared()
    {
        using var rt = await Load();
        string a = Doc("clouds");
        var first = await Run(rt, a);
        var saved = await rt.SaveCacheAsync("integration-test");
        Assert.True(saved.Tokens >= first.PromptTokens);
        Assert.Contains(rt.SavedCaches(), c => c.Name == "integration-test");

        await rt.ClearCacheAsync();
        Assert.Equal(0, (await Run(rt, a)).CachedTokens);        // really cold again
        await rt.ClearCacheAsync();

        int restored = await rt.RestoreCacheAsync("integration-test");
        Assert.True(restored >= first.PromptTokens);
        var warm = await Run(rt, a + " Again:");
        Assert.True(warm.CachedTokens >= first.PromptTokens - 2, $"restored cache gave only {warm.CachedTokens} cached tokens");
        PromptCacheStore.Delete(rt.Model, "integration-test");
    }

    [ModelFact]
    public async Task AutoSaveCarriesTheCacheAcrossUnloadAndLoad()
    {
        var pc = AppSettings.Current.PromptCache;
        bool before = pc.AutoSave;
        pc.AutoSave = true;
        try
        {
            string a = Doc("rivers");
            int promptTokens;
            using (var rt = await Load())
                promptTokens = (await Run(rt, a)).PromptTokens;       // disposing the runtime writes auto-0
            using var again = await Load();                           // loading it restores auto-0
            var warm = await Run(again, a + " Again:");
            Assert.True(warm.CachedTokens >= promptTokens - 2, $"auto-restored cache gave only {warm.CachedTokens} of {promptTokens}");
            foreach (var c in again.SavedCaches().Where(c => c.Name.StartsWith("auto-"))) PromptCacheStore.Delete(again.Model, c.Name);
        }
        finally { pc.AutoSave = before; }
    }
}

/// <summary>Speculative decoding must change speed, never the answer.</summary>
[Trait("Category", "Integration")]
[Collection("Models")]
public class SpeculativeDecodingTests
{
    private static string VlmPath => Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!;

    private static async Task<ModelRuntime> Load(bool draft, int tokens = 5)
    {
        var model = new ModelLibrary().Find(VlmPath)!;
        var o = new LoadOptions
        {
            Device = "cpu", ContextSize = 2048, MaxConcurrent = 1, Threads = 8, SkipFitCheck = true, Vision = false,
            DraftModel = draft ? VlmPath : null, DraftTokens = tokens, DraftMinP = 0f,
        };
        return await ModelRuntime.LoadAsync(model, o, "spec-test", null, CancellationToken.None);
    }

    private static async Task<(string Text, GenerationStats Stats)> Run(ModelRuntime rt, GenerationOptions g)
    {
        var prompt = rt.BuildFittingPrompt(new() { new ChatMessage("user", "Count from one to twenty in words, separated by commas.") }, 64, out _, out _, "off");
        var stats = new GenerationStats();
        return (await rt.CompleteAsync(prompt, g, stats), stats);
    }

    private static GenerationOptions Greedy(int max = 48) => new() { Temperature = 0, MaxTokens = max, ReasoningEffort = "off" };

    [VlmFact]
    public async Task GreedyOutputIsIdenticalAndEveryDraftIsAccepted()
    {
        string plain;
        using (var rt = await Load(draft: false)) plain = (await Run(rt, Greedy())).Text;

        using var spec = await Load(draft: true);
        Assert.NotNull(spec.DraftModelId);
        var (text, stats) = await Run(spec, Greedy());

        Assert.Equal(plain, text);
        Assert.True(stats.DraftTokens > 0, "no tokens were drafted");
        Assert.Equal(stats.DraftTokens, stats.DraftAccepted);       // the draft is the model itself: always right at temperature 0
    }

    [VlmFact]
    public async Task WrongDraftsAreRolledBackWithoutChangingTheOutput()
    {
        string plain;
        using (var rt = await Load(draft: false)) plain = (await Run(rt, Greedy())).Text;

        // damage every second proposal: the main model must reject it and carry on from the right token
        ModelRuntime.DraftMutator = drafts =>
        {
            var copy = new List<LLama.Native.LLamaToken>(drafts);
            for (int i = 1; i < copy.Count; i += 2) copy[i] = (LLama.Native.LLamaToken)(((int)copy[i] + 7) % 1000 + 3);
            return copy;
        };
        try
        {
            using var spec = await Load(draft: true, tokens: 6);
            var (text, stats) = await Run(spec, Greedy());
            Assert.Equal(plain, text);
            Assert.True(stats.DraftAccepted < stats.DraftTokens, "some drafts should have been rejected");
            Assert.True(stats.DraftAccepted > 0, "some drafts should have been accepted");

            // the cache must be consistent after the rollbacks: a follow-up turn gives the same answer as without speculation
            var (again, s2) = await Run(spec, Greedy());
            Assert.Equal(plain, again);
            Assert.True(s2.CachedTokens > 0);
        }
        finally { ModelRuntime.DraftMutator = null; }
    }

    [VlmFact]
    public async Task SampledOutputWithTheSameSeedMatchesToo()
    {
        var g = () => new GenerationOptions { Temperature = 0.8f, Seed = 11, MaxTokens = 40, ReasoningEffort = "off" };
        string plain;
        using (var rt = await Load(draft: false)) plain = (await Run(rt, g())).Text;
        using var spec = await Load(draft: true);
        Assert.Equal(plain, (await Run(spec, g())).Text);
    }

    [VlmFact]
    public async Task StopStringsAndTokenLimitsStillApply()
    {
        using var spec = await Load(draft: true);
        var (text, stats) = await Run(spec, new GenerationOptions { Temperature = 0, MaxTokens = 7, ReasoningEffort = "off" });
        Assert.Equal(7, stats.CompletionTokens);
        Assert.Equal("length", stats.FinishReason);
    }

    [VlmFact]
    public async Task AnIncompatibleDraftIsIgnoredWithAWarning()
    {
        // Qwen3 and SmolVLM use different tokenizers
        string? other = Environment.GetEnvironmentVariable("LLMHOST_TEST_MODEL");
        if (string.IsNullOrEmpty(other)) return;
        var model = new ModelLibrary().Find(VlmPath)!;
        var o = new LoadOptions { Device = "cpu", ContextSize = 1024, MaxConcurrent = 1, Threads = 8, SkipFitCheck = true, Vision = false, DraftModel = other };
        using var rt = await ModelRuntime.LoadAsync(model, o, "spec-bad", null, CancellationToken.None);
        Assert.Null(rt.DraftModelId);
        Assert.Contains(rt.LoadWarnings, w => w.Contains("Draft model"));
    }
}
