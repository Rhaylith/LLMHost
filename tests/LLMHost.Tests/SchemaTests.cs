using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class MigrationTests
{
    private static string Temp(string name)
    {
        string dir = Path.Combine(TestEnvironment.Home, "migrations", Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    [Fact]
    public void UnversionedSettingsAreStampedAndBackedUp()
    {
        string f = Temp("settings.json");
        File.WriteAllText(f, """{"port":4321,"host":"127.0.0.1"}""");

        Assert.Equal(MigrationResult.Migrated, Migrations.MigrateFile(f, Migrations.Settings));

        var o = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
        Assert.Equal(Migrations.CurrentVersion(Migrations.Settings), (int)o["schemaVersion"]!);
        Assert.Equal(4321, (int)o["port"]!);
        Assert.True(File.Exists(f + ".bak-v0"));
        Assert.DoesNotContain("schemaVersion", File.ReadAllText(f + ".bak-v0"));
        // second run is a no-op
        Assert.Equal(MigrationResult.UpToDate, Migrations.MigrateFile(f, Migrations.Settings));
    }

    [Fact]
    public void LegacyDictionaryFilesAreWrapped()
    {
        string f = Temp("presets.json");
        File.WriteAllText(f, """{"creative":{"temperature":1.2},"schemaVersion":{"temperature":0.1}}""");

        Assert.Equal(MigrationResult.Migrated, Migrations.MigrateFile(f, Migrations.Presets));

        var o = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
        Assert.Equal(1, (int)o["schemaVersion"]!);
        Assert.Equal(1.2, (double)o["items"]!["creative"]!["temperature"]!, 3);
        // a preset that happens to be called "schemaVersion" is data, not a version
        Assert.NotNull(o["items"]!["schemaVersion"]);
        Assert.Equal(2, Migrations.ReadItems<GenerationOptions>(f, Migrations.Presets).Count);
    }

    [Fact]
    public void NewerFilesAreKeptNotDowngradedSilently()
    {
        string f = Temp("chat.json");
        File.WriteAllText(f, """{"schemaVersion":99,"title":"future"}""");

        Assert.Equal(MigrationResult.NewerThanApp, Migrations.MigrateFile(f, Migrations.Chat));

        Assert.Contains("future", File.ReadAllText(f + ".bak-v99"));
        Assert.Contains("99", File.ReadAllText(f));   // untouched
    }

    [Fact]
    public void InvalidJsonIsQuarantined()
    {
        string f = Temp("settings.json");
        File.WriteAllText(f, "{ not json");

        Assert.Equal(MigrationResult.Invalid, Migrations.MigrateFile(f, Migrations.Settings));
        Assert.Equal("{ not json", File.ReadAllText(f + ".invalid"));
    }

    [Fact]
    public void ItemsRoundTripThroughTheVersionedFormat()
    {
        string f = Temp("model-defaults.json");
        Migrations.WriteItems(f, Migrations.ModelDefaults, new Dictionary<string, ModelConfig>
        {
            ["a/b/c.gguf"] = new() { Load = new LoadOptions { ContextSize = 8192 } },
        });
        var back = Migrations.ReadItems<ModelConfig>(f, Migrations.ModelDefaults);
        Assert.Equal(8192u, back["a/b/c.gguf"].Load!.ContextSize);
        Assert.Equal(1, (int)JsonNode.Parse(File.ReadAllText(f))!["schemaVersion"]!);
    }

    [Fact]
    public void UnknownKeysAreFoundAtAnyDepth()
    {
        var node = JsonNode.Parse("""{"port":1,"bogus":5,"defaultLoad":{"contextSize":1,"fooBar":true},"defaultGeneration":{"temperature":1}}""");
        var unknown = Migrations.FindUnknownKeys(node, typeof(AppSettings));
        Assert.Equal(new[] { "bogus", "defaultLoad.fooBar" }, unknown.Keys.OrderBy(k => k).ToArray());
    }
}

[Trait("Category", "Unit")]
public class SettingsValidatorTests
{
    [Fact]
    public void DefaultsAreValid() => Assert.Empty(SettingsValidator.Validate(new AppSettings()));

    [Fact]
    public void ReportsEachBadFieldByPath()
    {
        var s = new AppSettings { Port = 70000, Backend = "cuda", UpdateFeedUrl = "ftp://x", LoadGuardrail = "maybe" };
        s.DefaultLoad.ContextSize = 3;
        s.DefaultLoad.KvCacheTypeK = "q2";
        s.DefaultGeneration.Temperature = -1;
        s.DefaultGeneration.ReasoningEffort = "extreme";

        var fields = SettingsValidator.Validate(s).Select(e => e.Field).ToHashSet();

        foreach (var f in new[] { "port", "backend", "updateFeedUrl", "loadGuardrail", "defaultLoad.contextSize", "defaultLoad.kvCacheTypeK",
                                  "defaultGeneration.temperature", "defaultGeneration.reasoningEffort" })
            Assert.Contains(f, fields);
    }

    [Fact]
    public void ContextZeroMeansModelDefault()
    {
        var s = new AppSettings();
        s.DefaultLoad.ContextSize = 0;
        Assert.Empty(SettingsValidator.Validate(s));
    }
}

[Trait("Category", "Api")]
public class SettingsApiValidationTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public SettingsApiValidationTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task PutWithInvalidFieldsReturns400WithFieldList()
    {
        var current = JsonSerializer.SerializeToNode(AppSettings.Current, Json.Options)!;
        current["port"] = 0;
        current["defaultLoad"]!["maxConcurrent"] = 99;

        var r = await _f.Http.PutAsJsonAsync("/api/settings", current);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        Assert.Equal("validation_error", (string)j["error"]!["type"]!);
        var fields = j["errors"]!.AsArray().Select(e => (string)e!["field"]!).ToList();
        Assert.Contains("port", fields);
        Assert.Contains("defaultLoad.maxConcurrent", fields);
        Assert.NotEqual(0, AppSettings.Current.Port);   // nothing was applied
    }
}
