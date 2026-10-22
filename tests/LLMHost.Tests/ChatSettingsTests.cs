using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class SettingsResolverTests
{
    private static GenerationOptions Gen(float temp, int topK = 40, string? system = null) => new() { Temperature = temp, TopK = topK, SystemPrompt = system };

    [Fact]
    public void WithNothingElseTheGlobalSettingsApply()
    {
        var e = SettingsResolver.Effective(null, null, null, Gen(0.3f));
        Assert.Equal(0.3f, e.Generation.Temperature);
        Assert.Equal("global", e.BaseLayer);
        Assert.Equal("global", e.Sources["temperature"]);
    }

    [Fact]
    public void AModelDefaultBeatsGlobalAndAPresetBeatsTheModelDefault()
    {
        var model = SettingsResolver.Effective(null, null, Gen(0.5f), Gen(0.3f));
        Assert.Equal(0.5f, model.Generation.Temperature);
        Assert.Equal("model", model.BaseLayer);

        var preset = SettingsResolver.Effective(null, Gen(0.9f), Gen(0.5f), Gen(0.3f));
        Assert.Equal(0.9f, preset.Generation.Temperature);
        Assert.Equal("preset", preset.Sources["temperature"]);
    }

    [Fact]
    public void TheChatChangesOnlyItsOwnFieldsAndTheRequestBeatsTheChat()
    {
        var chat = new JsonObject { ["temperature"] = 1.2, ["topK"] = 10 };
        var e = SettingsResolver.Effective(chat, Gen(0.9f, topK: 40), null, Gen(0.3f));
        Assert.Equal(1.2f, e.Generation.Temperature);
        Assert.Equal(10, e.Generation.TopK);
        Assert.Equal("chat", e.Sources["temperature"]);
        Assert.Equal("preset", e.Sources["topP"]);                       // untouched fields keep coming from the base

        var req = SettingsResolver.Effective(chat, Gen(0.9f), null, Gen(0.3f), new JsonObject { ["temperature"] = 0.0 });
        Assert.Equal(0f, req.Generation.Temperature);
        Assert.Equal("request", req.Sources["temperature"]);
        Assert.Equal(10, req.Generation.TopK);
    }

    [Fact]
    public void ADiffKeepsOnlyWhatChangedAndIgnoresUnknownKeys()
    {
        var baseSet = Gen(0.7f, topK: 40, system: "Be brief.");
        var wanted = SettingsResolver.ToJson(baseSet);
        wanted["temperature"] = 0.699999988079071;                      // the float written back as a double is not a change
        wanted["topK"] = 12;
        wanted["bogus"] = "x";
        wanted["systemPrompt"] = "Be long.";
        var d = SettingsResolver.Diff(baseSet, wanted);
        Assert.Equal(new[] { "systemPrompt", "topK" }, d.Select(kv => kv.Key).OrderBy(k => k));
        Assert.Equal(12, (int)d["topK"]!);
    }

    [Fact]
    public void ReturningToTheBaseValueRemovesTheOverride()
    {
        var baseSet = Gen(0.7f);
        var wanted = SettingsResolver.ToJson(baseSet);
        Assert.Empty(SettingsResolver.Diff(baseSet, wanted));
        wanted["temperature"] = 1.5;
        Assert.Single(SettingsResolver.Diff(baseSet, wanted));
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class ChatSettingsApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ChatSettingsApiTests(ServerFixture f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<JsonNode> Json(HttpResponseMessage r)
    {
        string text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!;
    }

    private async Task<string> Save(string id, string settingsJson, string model = "")
    {
        string m = model.Length == 0 ? "" : $"\"model\":\"{model}\",";
        await Json(await _f.Http.PutAsync("/api/chats/" + id, Body($$"""{"title":"t",{{m}}"messages":[{"id":"a","role":"user","content":"hi"}],"settings":{{settingsJson}}}""")));
        return id;
    }

    [Fact]
    public async Task OnlyTheFieldsThatDifferFromTheBaseAreStoredAsChatSettings()
    {
        string id = "cs" + Guid.NewGuid().ToString("N")[..8];
        var baseNow = AppSettings.Current.DefaultGeneration;
        var full = SettingsResolver.ToJson(baseNow);
        full["temperature"] = 1.9;
        full["topK"] = 7;
        await Save(id, new JsonObject { ["generation"] = full }.ToJsonString());

        var e = await Json(await _f.Http.GetAsync($"/api/effective-settings?chat={id}"));
        Assert.Equal(2, ((JsonObject)e["overrides"]!).Count);
        Assert.Equal(1.9, (double)e["effective"]!["temperature"]!, 3);
        Assert.Equal(7, (int)e["effective"]!["topK"]!);
        Assert.Equal("chat", (string)e["sources"]!["topK"]!);
        Assert.Equal("global", (string)e["sources"]!["topP"]!);
        Assert.Equal("global", (string)e["baseLayer"]!);
        Assert.NotEqual(7, (int)e["base"]!["topK"]!);

        // saving the messages without a settings block keeps them
        await Json(await _f.Http.PutAsync("/api/chats/" + id, Body("""{"title":"t","messages":[{"id":"a","role":"user","content":"hi"}]}""")));
        var again = await Json(await _f.Http.GetAsync($"/api/effective-settings?chat={id}"));
        Assert.Equal(2, ((JsonObject)again["overrides"]!).Count);
        await _f.Http.DeleteAsync("/api/chats/" + id);
    }

    [Fact]
    public async Task APresetBecomesTheBaseAndResetDropsTheChatsChanges()
    {
        string name = "preset-" + Guid.NewGuid().ToString("N")[..6];
        var p = SettingsResolver.ToJson(AppSettings.Current.DefaultGeneration);
        p["temperature"] = 0.11; p["topK"] = 5;
        await Json(await _f.Http.PutAsync("/api/presets/" + name, Body(p.ToJsonString())));

        string id = "cp" + Guid.NewGuid().ToString("N")[..8];
        var form = (JsonObject)p.DeepClone();
        form["topK"] = 99;                                                       // the chat changes one field of the preset
        await Save(id, new JsonObject { ["preset"] = name, ["generation"] = form }.ToJsonString());

        var e = await Json(await _f.Http.GetAsync($"/api/effective-settings?chat={id}"));
        Assert.Equal("preset", (string)e["baseLayer"]!);
        Assert.Equal(0.11, (double)e["effective"]!["temperature"]!, 3);
        Assert.Equal("preset", (string)e["sources"]!["temperature"]!);
        Assert.Equal(99, (int)e["effective"]!["topK"]!);
        Assert.Equal(new[] { "topK" }, ((JsonObject)e["overrides"]!).Select(kv => kv.Key).ToArray());

        var reset = await Json(await _f.Http.PostAsync($"/api/chats/{id}/settings/reset", null));
        Assert.Empty((JsonObject)reset["overrides"]!);
        Assert.Equal(5, (int)reset["effective"]!["topK"]!);
        Assert.Equal(name, (string)reset["preset"]!);                           // the preset stays unless ?preset=1

        await _f.Http.PostAsync($"/api/chats/{id}/settings/reset?preset=1", null);
        Assert.Equal("global", (string)(await Json(await _f.Http.GetAsync($"/api/effective-settings?chat={id}")))["baseLayer"]!);
        await _f.Http.DeleteAsync("/api/presets/" + name);
        await _f.Http.DeleteAsync("/api/chats/" + id);
    }

    [Fact]
    public async Task TheChatsValuesCanBeSavedAsANewPreset()
    {
        string id = "cn" + Guid.NewGuid().ToString("N")[..8];
        var full = SettingsResolver.ToJson(AppSettings.Current.DefaultGeneration);
        full["temperature"] = 0.05;
        await Save(id, new JsonObject { ["generation"] = full }.ToJsonString());

        string name = "mine-" + Guid.NewGuid().ToString("N")[..6];
        var res = await Json(await _f.Http.PostAsync($"/api/chats/{id}/settings/save-as", Body($$"""{"target":"preset","name":"{{name}}"}""")));
        Assert.Equal(name, (string)res["preset"]!);                              // the chat now follows the preset it created...
        Assert.Empty((JsonObject)res["overrides"]!);                             // ...with nothing left to override
        var presets = await Json(await _f.Http.GetAsync("/api/presets"));
        Assert.Equal(0.05, (double)presets[name]!["temperature"]!, 3);

        var bad = await _f.Http.PostAsync($"/api/chats/{id}/settings/save-as", Body("""{"target":"preset","name":" "}"""));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        var bad2 = await _f.Http.PostAsync($"/api/chats/{id}/settings/save-as", Body("""{"target":"nothing"}"""));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad2.StatusCode);
        await _f.Http.DeleteAsync("/api/presets/" + name);
        await _f.Http.DeleteAsync("/api/chats/" + id);
    }

    [Fact]
    public async Task TheChatsValuesCanBeSavedAsTheDefaultOfItsModel()
    {
        string model = "fake/model-" + Guid.NewGuid().ToString("N")[..6] + ".gguf";
        string id = "cm" + Guid.NewGuid().ToString("N")[..8];
        var full = SettingsResolver.ToJson(AppSettings.Current.DefaultGeneration);
        full["temperature"] = 0.42;
        await Save(id, new JsonObject { ["generation"] = full }.ToJsonString(), model);
        await Json(await _f.Http.PostAsync($"/api/chats/{id}/settings/save-as", Body("""{"target":"modelDefault"}""")));

        var e = await Json(await _f.Http.GetAsync($"/api/effective-settings?chat={id}"));
        Assert.Equal("model", (string)e["baseLayer"]!);
        Assert.Equal(0.42, (double)e["base"]!["temperature"]!, 3);
        _f.Host.ModelDefaults.Clear(model, "generation");
        await _f.Http.DeleteAsync("/api/chats/" + id);
    }

    [Fact]
    public async Task AnUnsavedChatCanStillAskForTheBaseOfAPresetAndModel()
    {
        var e = await Json(await _f.Http.GetAsync("/api/effective-settings?model=nothing-loaded&preset=does-not-exist"));
        Assert.Equal("global", (string)e["baseLayer"]!);                         // unknown preset / model fall back to the global settings
        Assert.Empty((JsonObject)e["overrides"]!);
        Assert.NotNull(e["effective"]!["temperature"]);
    }
}
