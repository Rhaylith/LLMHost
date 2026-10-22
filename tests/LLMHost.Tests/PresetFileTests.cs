using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class PresetRulesTests
{
    [Theory]
    [InlineData("Creative writing", "Creative writing")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("a/b\\c:d*e?f", "a-b-c-d-e-f")]
    [InlineData("...", null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("CON", "CON-preset")]
    [InlineData("Ünïcode ✓", "Ünïcode")]
    public void NamesAreMadeSafeForFiles(string input, string? expected) => Assert.Equal(expected, PresetRules.SafeName(input));

    [Fact]
    public void LongNamesAreCut() => Assert.True(PresetRules.SafeName(new string('x', 200))!.Length <= 60);

    [Fact]
    public void LoadSettingsAreDroppedAndUnknownKeysReported()
    {
        const string json = """
            {"schemaVersion":1,"name":"Mixed","description":"d","systemPrompt":"Be brief.",
             "load":{"contextSize":4096},
             "inference":{"temperature":0.2,"topK":10,"contextSize":8192,"gpuLayers":20,"flashAttentionMode":"on","fancy":1}}
            """;
        var r = PresetRules.Check(json);
        Assert.Null(r.Error);
        Assert.Contains("load", r.Dropped);
        Assert.Contains("inference.contextSize", r.Dropped);
        Assert.Contains("inference.gpuLayers", r.Dropped);
        Assert.Contains("inference.fancy", r.Unknown);
        var g = r.Preset!.ToGeneration();
        Assert.Equal(0.2f, g.Temperature);
        Assert.Equal(10, g.TopK);
        Assert.Equal("Be brief.", g.SystemPrompt);
        Assert.False(r.Preset.Inference.ContainsKey("contextSize"));
        Assert.False(r.Preset.Inference.ContainsKey("fancy"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"name":"x","inference":{"temperature":"hot"}}""")]
    [InlineData("""{"name":"...","inference":{}}""")]
    public void BrokenDocumentsAreRefused(string json) => Assert.NotNull(PresetRules.Check(json).Error);

    [Fact]
    public void ANameFallsBackToTheFileName() => Assert.Equal("from-file", PresetRules.Check("""{"inference":{"temperature":0.3}}""", "from-file").Preset!.Name);
}

[Trait("Category", "Unit")]
public class PresetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmhost-presets-" + Guid.NewGuid().ToString("N")[..8]);
    private PresetStore Store() => new(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void APresetRoundTripsAsOneFile()
    {
        var s = Store();
        var g = new GenerationOptions { Temperature = 0.33f, TopK = 12, SystemPrompt = "You are a pirate." };
        string name = s.Save("Pirate talk", g, "arr");
        Assert.Equal("Pirate talk", name);
        Assert.True(File.Exists(Path.Combine(_dir, "Pirate talk.json")));

        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "Pirate talk.json")))!;
        Assert.Equal(1, (int)doc["schemaVersion"]!);
        Assert.Equal("arr", (string)doc["description"]!);
        Assert.Equal("You are a pirate.", (string)doc["systemPrompt"]!);
        Assert.Null(doc["inference"]!["systemPrompt"]);                              // the prompt lives at the top level
        Assert.Equal(12, (int)doc["inference"]!["topK"]!);

        var back = Store().All()["Pirate talk"];
        Assert.Equal(0.33f, back.Temperature);
        Assert.Equal("You are a pirate.", back.SystemPrompt);
        Assert.Equal("arr", Store().Get("pirate TALK")!.Description);                // names compare without case
    }

    [Fact]
    public void ASaveKeepsTheDescriptionUnlessAnotherIsGiven()
    {
        var s = Store();
        s.Save("p", new GenerationOptions(), "first");
        s.Save("p", new GenerationOptions { Temperature = 1.1f });
        Assert.Equal("first", s.Get("p")!.Description);
        s.Save("p", new GenerationOptions(), "second");
        Assert.Equal("second", s.Get("p")!.Description);
    }

    [Fact]
    public void ANameCannotEscapeTheFolder()
    {
        var s = Store();
        string name = s.Save("../../evil", new GenerationOptions());
        Assert.Equal("evil", name);
        Assert.Equal(new[] { "evil.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(_dir, "..", "..", "evil.json")));
        Assert.Throws<ArgumentException>(() => s.Save("...", new GenerationOptions()));
        Assert.False(s.Delete("../../evil"));                                        // only an exact existing preset is deleted
        Assert.True(s.Delete("evil"));
    }

    [Fact]
    public void FilesEditedByHandAreReadAndBrokenOnesSkipped()
    {
        var s = Store();
        File.WriteAllText(Path.Combine(_dir, "hand.json"), """{"name":"By hand","inference":{"temperature":0.9,"contextSize":999}}""");
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");
        var all = s.All();
        Assert.Equal(new[] { "By hand" }, all.Keys);
        Assert.Equal(0.9f, all["By hand"].Temperature);
    }

    [Fact]
    public async Task AChangeOnDiskBumpsTheVersion()
    {
        var s = Store();
        int v0 = s.Version;
        File.WriteAllText(Path.Combine(_dir, "live.json"), """{"name":"Live","inference":{}}""");
        for (int i = 0; i < 40 && s.Version == v0; i++) await Task.Delay(100);
        Assert.NotEqual(v0, s.Version);
        Assert.Contains("Live", s.All().Keys);
    }

    [Fact]
    public void DuplicatingPicksAFreeName()
    {
        var s = Store();
        s.Save("base", new GenerationOptions { TopK = 5 }, "desc");
        Assert.Equal("base (copy)", s.Duplicate("base"));
        Assert.Equal("base (copy 2)", s.Duplicate("base"));
        Assert.Equal("renamed", s.Duplicate("base", "renamed"));
        Assert.Equal(5, s.All()["renamed"].TopK);
        Assert.Equal("desc", s.Get("renamed")!.Description);
        Assert.Throws<InvalidOperationException>(() => s.Duplicate("base", "renamed"));
        Assert.Throws<FileNotFoundException>(() => s.Duplicate("nothing"));
    }
}

[Trait("Category", "Unit")]
[Collection("Settings")]
public class PresetMigrationTests
{
    [Fact]
    public void TheOldPresetsJsonBecomesOneFileEachAndIsKept()
    {
        AppPaths.EnsureCreated();
        string old = AppPaths.PresetsFile, dir = AppPaths.PresetsDir;
        string? backup = File.Exists(old) ? File.ReadAllText(old) : null;
        var existing = Directory.Exists(dir) ? Directory.GetFiles(dir).ToList() : new();
        try
        {
            foreach (var f in existing) File.Move(f, f + ".hold");
            File.WriteAllText(old, """{"creative":{"temperature":1.3},"strict/one":{"temperature":0.1,"systemPrompt":"Be exact."}}""");
            var store = new PresetStore();
            var all = store.All();
            Assert.Equal(1.3f, all["creative"].Temperature);
            Assert.Equal("Be exact.", all["strict-one"].SystemPrompt);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(old + ".migrated"));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith(".hold"))) File.Delete(f);
            foreach (var f in Directory.GetFiles(dir, "*.hold")) File.Move(f, f[..^5]);
            File.Delete(old + ".migrated");
            if (backup != null) File.WriteAllText(old, backup);
        }
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class PresetApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public PresetApiTests(ServerFixture f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task CreateReadDuplicateAndDelete()
    {
        string n = "api-" + Guid.NewGuid().ToString("N")[..6];
        var put = await _f.Http.PutAsync("/api/presets/" + n, Body("""{"temperature":0.4,"topK":22,"systemPrompt":"Hi","description":"from the test"}"""));
        Assert.True(put.IsSuccessStatusCode);

        var one = JsonNode.Parse(await _f.Http.GetStringAsync("/api/presets/" + n))!;
        Assert.Equal("from the test", (string)one["description"]!);
        Assert.Equal("Hi", (string)one["systemPrompt"]!);
        Assert.Equal(22, (int)one["inference"]!["topK"]!);

        var dup = JsonNode.Parse(await (await _f.Http.PostAsync($"/api/presets/{n}/duplicate", Body("{}"))).Content.ReadAsStringAsync())!;
        Assert.Equal(n + " (copy)", (string)dup["name"]!);

        var list = JsonNode.Parse(await _f.Http.GetStringAsync("/api/preset-list"))!;
        Assert.Contains(list["presets"]!.AsArray(), p => (string)p!["name"]! == n);
        Assert.True((int)list["version"]! >= 0);

        Assert.Equal(0.4, (double)JsonNode.Parse(await _f.Http.GetStringAsync("/api/presets"))![n]!["temperature"]!, 3);

        foreach (var name in new[] { n, n + " (copy)" })
            Assert.True((bool)JsonNode.Parse(await (await _f.Http.DeleteAsync("/api/presets/" + Uri.EscapeDataString(name))).Content.ReadAsStringAsync())!["deleted"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync("/api/presets/" + n)).StatusCode);
    }
}
