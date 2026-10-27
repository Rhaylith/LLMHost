using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class ClaudeRemoteSettingsUnitTests
{
    [Fact]
    public void TheFileHasTheSameEntriesAsTheLocalOneButTheGivenAddressAndToken()
    {
        var j = JsonNode.Parse(ClaudeCodeLauncher.RemoteSettings("http://my-pc:1234/", "ait_secret", "m1"))!;
        Assert.Equal("m1", (string)j["model"]!);
        var env = j["env"]!.AsObject();
        Assert.Equal("http://my-pc:1234", (string)env["ANTHROPIC_BASE_URL"]!);       // no trailing slash
        Assert.Equal("ait_secret", (string)env["ANTHROPIC_AUTH_TOKEN"]!);
        foreach (var k in new[] { "ANTHROPIC_MODEL", "ANTHROPIC_SMALL_FAST_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "CLAUDE_CODE_SUBAGENT_MODEL" })
            Assert.Equal("m1", (string)env[k]!);
        Assert.Equal("1", (string)env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]!);
    }

    [Fact]
    public void TheSuggestedAddressUsesTheMachineNameAndThePort()
    {
        Assert.Equal($"http://{Environment.MachineName.ToLowerInvariant()}:1234", ClaudeCodeLauncher.SuggestRemoteAddress(1234));
        Assert.StartsWith("https://", ClaudeCodeLauncher.SuggestRemoteAddress(1235, true));
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class ClaudeRemoteSettingsApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ClaudeRemoteSettingsApiTests(ServerFixture f) => _f = f;
    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task WithoutALoadedModelThereIsNothingToWrite()
    {
        _f.Host.Models.UnloadAll();
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.PostAsync("/api/launch/claude-code/remote-settings", Body("""{"address":"http://pc:1234"}"""))).StatusCode);
    }

    [Fact]
    public async Task TheAddressSuggestionComesWithWarnings()
    {
        var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/launch/claude-code/remote-address"))!;
        Assert.StartsWith("http://", (string)j["address"]!);
        Assert.NotEmpty(j["warnings"]!.AsArray());                  // the test server listens on loopback and has no authentication
    }

    [ModelFact]
    public async Task AFileIsMadeForALoadedModelAndRefusesAddressesThatPointBackAtTheSameComputer()
    {
        _f.Host.Models.UnloadAll();
        await _f.Host.Models.LoadAsync(Environment.GetEnvironmentVariable("LLMHOST_TEST_MODEL")!, new LoadOptions { Device = "cpu", ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Identifier = "remote-file" });
        try
        {
            foreach (var bad in new[] { "http://localhost:1234", "http://127.0.0.1:1234", "my-pc:1234", "ftp://pc:1234", "http://pc:1234/v1" })
                Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.PostAsync("/api/launch/claude-code/remote-settings", Body($"{{\"address\":\"{bad}\"}}"))).StatusCode);

            var r = JsonNode.Parse(await (await _f.Http.PostAsync("/api/launch/claude-code/remote-settings", Body("""{"address":"http://my-pc:4321"}"""))).Content.ReadAsStringAsync())!;
            Assert.Equal("settings.local.json", (string)r["fileName"]!);
            var file = JsonNode.Parse((string)r["json"]!)!;
            Assert.Equal("remote-file", (string)file["model"]!);
            Assert.Equal("http://my-pc:4321", (string)file["env"]!["ANTHROPIC_BASE_URL"]!);
            Assert.Equal("local", (string)file["env"]!["ANTHROPIC_AUTH_TOKEN"]!);       // authentication is off, so there is no token to hand out
        }
        finally { _f.Host.Models.UnloadAll(); }
    }

    [ModelFact]
    public async Task WithAuthenticationOnAChatOnlyTokenIsCreatedAndPutInTheFile()
    {
        _f.Host.Models.UnloadAll();
        await _f.Host.Models.LoadAsync(Environment.GetEnvironmentVariable("LLMHOST_TEST_MODEL")!, new LoadOptions { Device = "cpu", ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Identifier = "remote-auth" });
        var old = AppSettings.Current.RequireAuth;
        try
        {
            AppSettings.Current.RequireAuth = true;
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/launch/claude-code/remote-settings") { Content = Body("""{"address":"http://my-pc:4321"}""") };
            req.Headers.Add("Authorization", "Bearer " + _f.Server.Auth.UiToken);
            var r = JsonNode.Parse(await (await _f.Http.SendAsync(req)).Content.ReadAsStringAsync())!;
            string secret = (string)JsonNode.Parse((string)r["json"]!)!["env"]!["ANTHROPIC_AUTH_TOKEN"]!;
            Assert.StartsWith("ait_", secret);
            var token = _f.Server.Auth.List().Single(t => t.Name == (string)r["tokenName"]!);
            Assert.Equal(new[] { "chat" }, token.Permissions);
            _f.Server.Auth.Revoke(token.Id);
        }
        finally { AppSettings.Current.RequireAuth = old; _f.Host.Models.UnloadAll(); }
    }
}
