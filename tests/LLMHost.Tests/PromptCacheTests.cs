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
