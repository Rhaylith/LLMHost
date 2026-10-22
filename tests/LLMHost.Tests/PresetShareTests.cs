using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class PresetShareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "llmhost-share-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Canned : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Urls.Add(r.RequestUri!.ToString()); return Task.FromResult(Reply(r)); }
    }

    private static HttpResponseMessage Json(string body, string type = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    [Theory]
    [InlineData("https://github.com/me/repo/blob/main/presets/a.preset.json", "https://raw.githubusercontent.com/me/repo/main/presets/a.preset.json")]
    [InlineData("https://example.com/x.json", "https://example.com/x.json")]
    public void GitHubBlobLinksBecomeRawLinks(string input, string expected) => Assert.Equal(expected, PresetShare.RewriteGitHub(input));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.5", false)]
    [InlineData("172.32.0.5", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.0.1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void OnlyPublicAddressesAreContacted(string ip, bool ok) => Assert.Equal(ok, PresetShare.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public async Task OnlyHttpsAddressesAreFetched()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PresetShare.FetchAsync("http://example.com/p.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => PresetShare.FetchAsync("file:///c:/p.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => PresetShare.FetchAsync("not a url"));
    }

    [Fact]
    public async Task AnAddressInThePrivateNetworkIsNeverConnected()
    {
        // the check runs when the connection is made, so this fails before any request is sent
        await Assert.ThrowsAsync<HttpRequestException>(() => PresetShare.FetchAsync("https://127.0.0.1:9/p.json"));
        await Assert.ThrowsAsync<HttpRequestException>(() => PresetShare.FetchAsync("https://localhost:9/p.json"));
    }

    [Fact]
    public async Task JsonAndPlainTextAreAcceptedOtherContentIsNot()
    {
        var h = new Canned();
        using var client = new HttpClient(h);
        h.Reply = _ => Json("""{"name":"ok"}""");
        Assert.Contains("ok", await PresetShare.FetchAsync("https://example.com/a.json", client: client));
        h.Reply = _ => Json("""{"name":"raw"}""", "text/plain");
        Assert.Contains("raw", await PresetShare.FetchAsync("https://raw.githubusercontent.com/a/b/main/p.json", client: client));
        h.Reply = _ => Json("<html></html>", "text/html");
        await Assert.ThrowsAsync<InvalidDataException>(() => PresetShare.FetchAsync("https://example.com/page", client: client));
    }

    [Fact]
    public async Task ABigFileIsRefusedWhileItIsRead()
    {
        var h = new Canned { Reply = _ => Json(new string('x', PresetShare.MaxBytes + 10)) };
        using var client = new HttpClient(h);
        await Assert.ThrowsAsync<InvalidDataException>(() => PresetShare.FetchAsync("https://example.com/big.json", client: client));
    }

    [Fact]
    public async Task RedirectsAreFollowedOnlyOverHttpsAndOnlyAFewTimes()
    {
        var h = new Canned();
        using var client = new HttpClient(h);
        h.Reply = r => r.RequestUri!.AbsolutePath == "/a"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/b") } }
            : Json("""{"name":"end"}""");
        Assert.Contains("end", await PresetShare.FetchAsync("https://example.com/a", client: client));
        Assert.Equal(new[] { "https://example.com/a", "https://example.com/b" }, h.Urls);

        h.Reply = _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://example.com/plain") } };
        await Assert.ThrowsAsync<ArgumentException>(() => PresetShare.FetchAsync("https://example.com/a", client: client));
        h.Reply = _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/loop") } };
        await Assert.ThrowsAsync<InvalidDataException>(() => PresetShare.FetchAsync("https://example.com/loop", client: client));
    }

    [Fact]
    public void ThePreviewListsWhatWillBeSetAndWhatIsIgnored()
    {
        var store = new PresetStore(_dir);
        const string json = """{"name":"Shared","description":"d","systemPrompt":"S","inference":{"temperature":0.1,"gpuLayers":9,"bogus":1}}""";
        var p = PresetShare.Preview(json, store);
        Assert.Equal("Shared", p.Name);
        Assert.Equal("0.1", p.Fields["temperature"]);
        Assert.Contains("inference.gpuLayers", p.Dropped);
        Assert.Contains("inference.bogus", p.Unknown);
        Assert.False(p.Exists);
        Assert.Empty(store.All());                                      // a preview stores nothing
    }

    [Fact]
    public void ACollisionNeedsOverwriteOrANewName()
    {
        var store = new PresetStore(_dir);
        store.Save("Shared", new GenerationOptions { TopK = 1 });
        const string json = """{"name":"Shared","inference":{"topK":77}}""";
        var p = PresetShare.Preview(json, store);
        Assert.True(p.Exists);
        Assert.Equal("Shared (2)", p.SuggestedName);

        Assert.Throws<InvalidOperationException>(() => PresetShare.Import(json, store));
        Assert.Equal(1, store.All()["Shared"].TopK);
        Assert.Equal("Shared (2)", PresetShare.Import(json, store, name: "Shared (2)"));
        Assert.Equal("Shared", PresetShare.Import(json, store, overwrite: true));
        Assert.Equal(77, store.All()["Shared"].TopK);
    }

    [Fact]
    public void AnExportImportsBackUnchanged()
    {
        var a = new PresetStore(_dir);
        a.Save("Roundtrip", new GenerationOptions { Temperature = 0.25f, TopP = 0.5f, SystemPrompt = "Keep it short." }, "my preset");
        string file = PresetShare.Export(a.Get("Roundtrip")!);

        var b = new PresetStore(Path.Combine(_dir, "other"));
        Assert.Equal("Roundtrip", PresetShare.Import(file, b));
        var g = b.All()["Roundtrip"];
        Assert.Equal(0.25f, g.Temperature);
        Assert.Equal(0.5f, g.TopP);
        Assert.Equal("Keep it short.", g.SystemPrompt);
        Assert.Equal("my preset", b.Get("Roundtrip")!.Description);
    }

    [Fact]
    public void ABigOrBrokenDocumentIsRefusedBeforeAnythingIsStored()
    {
        var store = new PresetStore(_dir);
        Assert.Throws<ArgumentException>(() => PresetShare.Preview(new string(' ', PresetShare.MaxBytes + 1), store));
        Assert.Throws<ArgumentException>(() => PresetShare.Import("{ nope", store));
        Assert.Empty(store.All());
    }

    [Fact]
    public void AMissingNameFallsBackToTheFileName()
    {
        var store = new PresetStore(_dir);
        Assert.Equal("from-file", PresetShare.Import("""{"inference":{"topK":3}}""", store, fallbackName: "from-file"));
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class PresetShareApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public PresetShareApiTests(ServerFixture f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task PreviewThenImportThenExport()
    {
        string n = "share-" + Guid.NewGuid().ToString("N")[..6];
        string doc = new JsonObject { ["name"] = n, ["description"] = "d", ["inference"] = new JsonObject { ["temperature"] = 0.2, ["gpuLayers"] = 4 } }.ToJsonString();
        var pv = JsonNode.Parse(await (await _f.Http.PostAsync("/api/presets/import/preview", Body(new JsonObject { ["text"] = doc }.ToJsonString()))).Content.ReadAsStringAsync())!;
        Assert.Equal(n, (string)pv["name"]!);
        Assert.False((bool)pv["exists"]!);
        Assert.Contains("inference.gpuLayers", pv["dropped"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync("/api/presets/" + n)).StatusCode);          // nothing stored yet

        var imp = await _f.Http.PostAsync("/api/presets/import", Body(new JsonObject { ["text"] = doc }.ToJsonString()));
        Assert.True(imp.IsSuccessStatusCode);
        var again = await _f.Http.PostAsync("/api/presets/import", Body(new JsonObject { ["text"] = doc }.ToJsonString()));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var over = await _f.Http.PostAsync("/api/presets/import", Body(new JsonObject { ["text"] = doc, ["overwrite"] = true }.ToJsonString()));
        Assert.True(over.IsSuccessStatusCode);

        var exp = await _f.Http.GetAsync($"/api/presets/{n}/export");
        Assert.Contains($"filename=\"{n}.preset.json\"", exp.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(0.2, (double)JsonNode.Parse(await exp.Content.ReadAsStringAsync())!["inference"]!["temperature"]!, 3);
        await _f.Http.DeleteAsync("/api/presets/" + n);
    }

    [Theory]
    [InlineData("""{"url":"http://example.com/p.json"}""")]
    [InlineData("""{"url":"https://127.0.0.1:9/p.json"}""")]
    [InlineData("""{"text":"nonsense"}""")]
    [InlineData("""{}""")]
    public async Task UnsafeOrBrokenSourcesAreRefused(string body)
    {
        var r = await _f.Http.PostAsync("/api/presets/import/preview", Body(body));
        Assert.True((int)r.StatusCode is >= 400 and < 500 or 500, r.StatusCode.ToString());
        Assert.False(r.IsSuccessStatusCode);
    }
}
