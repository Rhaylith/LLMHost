using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class JitSettingsTests
{
    [Fact]
    public void TheValidatorChecksTheLimitsAndTheOrder()
    {
        var s = new AppSettings();
        Assert.DoesNotContain(SettingsValidator.Validate(s), e => e.Field.StartsWith("jit"));
        s.Jit.MaxLoadedModels = -1; s.Jit.DefaultTtlMinutes = -5; s.Jit.EvictOrder = "random";
        var fields = SettingsValidator.Validate(s).Select(e => e.Field).ToList();
        Assert.Contains("jit.maxLoadedModels", fields);
        Assert.Contains("jit.defaultTtlMinutes", fields);
        Assert.Contains("jit.evictOrder", fields);
    }

    [Fact]
    public void ByDefaultNothingIsLimitedAndNothingExpires()
    {
        var j = new JitSettings();
        Assert.True(j.Enabled && j.AutoEvict);
        Assert.Equal(0, j.MaxLoadedModels);
        Assert.Equal(0, j.DefaultTtlMinutes);
        Assert.Equal("lru", j.EvictOrder);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class JitIntegrationTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public JitIntegrationTests(ServerFixture f) => _f = f;

    private static string Chat => Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!;
    private static string Embed => Environment.GetEnvironmentVariable("LLMHOST_TEST_EMBED")!;
    private static readonly LoadOptions Small = new() { Device = "cpu", ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Vision = false };

    private ModelManager M => _f.Host.Models;

    private void Reset(Action<JitSettings>? tweak = null)
    {
        M.UnloadAll();
        AppSettings.Current.AutoLoadOnRequest = true;
        AppSettings.Current.Jit = new JitSettings();
        AppSettings.Current.DefaultLoad = Small.Clone();
        tweak?.Invoke(AppSettings.Current.Jit);
    }

    [VlmFact]
    public async Task ARequestLoadsTheModelAndMarksItAsOnDemand()
    {
        Reset();
        var rt = await M.ResolveAsync(Chat);
        Assert.Equal("jit", rt.LoadedBy);
        var manual = await M.LoadAsync(Chat, Small.Clone().With(o => o.Identifier = "manual-one"));
        Assert.Equal("manual", manual.LoadedBy);
        M.UnloadAll();
    }

    [VlmFact]
    public async Task TheDefaultIdleTimeAppliesOnlyToOnDemandModels()
    {
        Reset(j => j.DefaultTtlMinutes = 7);
        var jit = await M.ResolveAsync(Chat);
        Assert.Equal(7 * 60, ModelManager.EffectiveTtlSeconds(jit));
        jit.Options.TtlMinutes = 2;                                                  // the model's own idle time wins
        Assert.Equal(120, ModelManager.EffectiveTtlSeconds(jit));
        jit.TtlSeconds = 30;                                                         // and a request wins over both
        Assert.Equal(30, ModelManager.EffectiveTtlSeconds(jit));
        jit.TtlSeconds = 0;
        Assert.Equal(0, ModelManager.EffectiveTtlSeconds(jit));                      // 0 = keep it
        M.UnloadAll();

        Reset(j => j.DefaultTtlMinutes = 7);
        var manual = await M.LoadAsync(Chat, Small.Clone());
        Assert.Equal(0, ModelManager.EffectiveTtlSeconds(manual));
        M.UnloadAll();
    }

    [VlmFact]
    public async Task WhenTheLimitIsReachedTheIdleOnDemandModelMakesRoom()
    {
        Reset(j => j.MaxLoadedModels = 1);
        var first = await M.ResolveAsync(Chat);
        Assert.Equal("jit", first.LoadedBy);
        var second = await M.ResolveAsync(Embed, embeddings: true);
        Assert.Equal(new[] { second.Identifier }, M.Loaded.Select(r => r.Identifier));
        Assert.Equal("jit", second.LoadedBy);
        M.UnloadAll();
    }

    [VlmFact]
    public async Task AModelLoadedByHandIsNeverEvictedAndTheRequestIsBlocked()
    {
        Reset(j => j.MaxLoadedModels = 1);
        await M.LoadAsync(Chat, Small.Clone());
        var ex = await Assert.ThrowsAsync<ModelLoadException>(() => M.ResolveAsync(Embed, embeddings: true));
        Assert.Equal("model_load_blocked", ex.Kind);
        Assert.Contains("loaded by hand", ex.Message);
        Assert.Single(M.Loaded);
        M.UnloadAll();
    }

    [VlmFact]
    public async Task WithoutAutoEvictTheLimitJustBlocks()
    {
        Reset(j => { j.MaxLoadedModels = 1; j.AutoEvict = false; });
        await M.ResolveAsync(Chat);
        var ex = await Assert.ThrowsAsync<ModelLoadException>(() => M.ResolveAsync(Embed, embeddings: true));
        Assert.Equal("model_load_blocked", ex.Kind);
        M.UnloadAll();
    }

    [VlmFact]
    public async Task SwitchingJitOffMeansAnUnloadedModelIsNotLoaded()
    {
        Reset(j => j.Enabled = false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => M.ResolveAsync(Chat));
        Assert.Empty(M.Loaded);
    }

    [VlmFact]
    public async Task ABlockedLoadAnswersWith503AndTheTtlOfARequestIsKept()
    {
        Reset(j => j.MaxLoadedModels = 1);
        var manual = await M.LoadAsync(Chat, Small.Clone().With(o => o.Identifier = "ttl-model"));
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent($$"""{"model":"ttl-model","max_tokens":2,"messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-llmhost-ttl", "45");
        Assert.True((await _f.Http.SendAsync(req)).IsSuccessStatusCode);
        Assert.Equal(45, manual.TtlSeconds);                                         // header

        var body = await _f.Http.PostAsync("/v1/chat/completions", new StringContent("""{"model":"ttl-model","ttl":90,"max_tokens":2,"messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"));
        Assert.True(body.IsSuccessStatusCode);
        Assert.Equal(90, manual.TtlSeconds);                                         // body field

        var blocked = await _f.Http.PostAsync("/v1/chat/completions", new StringContent($$"""{"model":"{{Embed.Replace("\\", "\\\\")}}","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal("model_load_blocked", (string)JsonNode.Parse(await blocked.Content.ReadAsStringAsync())!["error"]!["type"]!);
        M.UnloadAll();
    }

    [VlmFact]
    public async Task TheStatusShowsOriginAndIdleTime()
    {
        Reset(j => j.DefaultTtlMinutes = 3);
        var rt = await M.ResolveAsync(Chat);
        var s = JsonNode.Parse(await _f.Http.GetStringAsync("/api/status"))!["loaded"]![0]!;
        Assert.Equal("jit", (string)s["loadedBy"]!);
        Assert.Equal(180, (int)s["ttlSeconds"]!);
        Assert.Equal(rt.Identifier, (string)s["id"]!);
        M.UnloadAll();
    }
}

internal static class LoadOptionsExtensions
{
    public static LoadOptions With(this LoadOptions o, Action<LoadOptions> change) { change(o); return o; }
}
