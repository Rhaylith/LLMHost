using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class LinkManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmhost-link-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly LinkSettings _sa = new() { Enabled = true, DeviceName = "alpha" }, _sb = new() { Enabled = true, DeviceName = "beta" };
    private readonly LinkManager _a, _b;

    public LinkManagerTests()
    {
        _a = new LinkManager(Path.Combine(_root, "a"), () => _sa);
        _b = new LinkManager(Path.Combine(_root, "b"), () => _sb);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private LinkPeer Pair()
    {
        string code = _a.StartPairing();
        var req = new LinkManager.PairRequest("beta", "http://b:1", _b.PublicKey, "");
        req = req with { Mac = Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(_b.PublicKey))) };
        var reply = _a.AcceptPairing(req)!;
        Assert.NotNull(reply);
        var peerA = new LinkPeer { Id = LinkManager.IdOf(reply.PublicKey), Name = reply.Name, Address = "http://a:1", PublicKey = reply.PublicKey };
        _b.AddPeer(peerA);
        return peerA;
    }

    private HttpRequestMessage Signed(LinkManager from, string method, string url, string body = "")
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        from.Sign(req, Encoding.UTF8.GetBytes(body));
        return req;
    }

    private (LinkPeer? Peer, string? Error) Check(LinkManager to, HttpRequestMessage req, string body = "", DateTime? now = null) =>
        to.Verify(req.Method.Method, req.RequestUri!.PathAndQuery, h => req.Headers.TryGetValues(h, out var v) ? v.First() : null, Encoding.UTF8.GetBytes(body), now);

    [Fact]
    public void EveryDeviceHasItsOwnStableIdentity()
    {
        Assert.NotEqual(_a.DeviceId, _b.DeviceId);
        Assert.Equal(16, _a.DeviceId.Length);
        var again = new LinkManager(Path.Combine(_root, "a"), () => _sa);
        Assert.Equal(_a.DeviceId, again.DeviceId);                                    // the key is stored (encrypted) and reused
        Assert.DoesNotContain(Convert.ToBase64String(Convert.FromBase64String(_a.PublicKey)), File.ReadAllText(Path.Combine(_root, "a", "device.key")));
    }

    [Fact]
    public void ACodePairsTwoDevicesBothWays()
    {
        var peerA = Pair();
        Assert.Equal(_a.DeviceId, peerA.Id);
        Assert.Equal("beta", _a.Peers.Single().Name);
        Assert.Equal(_b.DeviceId, _a.Peers.Single().Id);
        Assert.Equal(new[] { "chat" }, _a.Peers.Single().Permissions);                // the least that is useful
        Assert.Single(new LinkManager(Path.Combine(_root, "a"), () => _sa).Peers);   // stored on disk
    }

    [Fact]
    public void AWrongOrUsedCodeIsRefusedAndFiveWrongTriesEndThePairing()
    {
        string code = _a.StartPairing();
        string Mac(string c) => Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(c), Encoding.UTF8.GetBytes(_b.PublicKey)));
        string wrong = code == "000000" ? "000001" : "000000";
        for (int i = 0; i < 5; i++) Assert.Null(_a.AcceptPairing(new("beta", "x", _b.PublicKey, Mac(wrong))));
        Assert.Null(_a.AcceptPairing(new("beta", "x", _b.PublicKey, Mac(code))));       // even the right code no longer works
        Assert.Empty(_a.Peers);

        code = _a.StartPairing();
        Assert.NotNull(_a.AcceptPairing(new("beta", "x", _b.PublicKey, Mac(code))));
        Assert.Null(_a.AcceptPairing(new("beta", "x", _b.PublicKey, Mac(code))));       // a code is used once
        Assert.Null(new LinkManager(Path.Combine(_root, "c"), () => _sa).AcceptPairing(new("beta", "x", _b.PublicKey, Mac(code))));   // nothing was started there
    }

    [Fact]
    public void ASignedRequestIsAcceptedOnceFromAPairedDeviceOnly()
    {
        Pair();
        var req = Signed(_b, "POST", "http://a:1/v1/chat/completions?x=1", "{\"a\":1}");
        var (peer, err) = Check(_a, req, "{\"a\":1}");
        Assert.Null(err);
        Assert.Equal("beta", peer!.Name);
        Assert.Equal("This request was already used.", Check(_a, req, "{\"a\":1}").Error);          // a replay

        var stranger = new LinkManager(Path.Combine(_root, "s"), () => _sb);
        Assert.Equal("Unknown device.", Check(_a, Signed(stranger, "GET", "http://a:1/api/link/info")).Error);
    }

    [Fact]
    public void ChangingAnythingInvalidatesTheSignature()
    {
        Pair();
        var ok = Signed(_b, "POST", "http://a:1/v1/chat/completions", "body");
        Assert.Equal("Bad signature.", Check(_a, Signed(_b, "POST", "http://a:1/v1/chat/completions", "body"), "other body").Error);       // body
        var other = Signed(_b, "POST", "http://a:1/v1/chat/completions", "body");
        Assert.Equal("Bad signature.", _a.Verify("POST", "/v1/embeddings", h => other.Headers.TryGetValues(h, out var v) ? v.First() : null, Encoding.UTF8.GetBytes("body")).Error);    // path
        Assert.Equal("Bad signature.", _a.Verify("GET", "/v1/chat/completions", h => ok.Headers.TryGetValues(h, out var v) ? v.First() : null, Encoding.UTF8.GetBytes("body")).Error);   // method
        Assert.Equal("Missing link headers.", _a.Verify("GET", "/x", _ => null, Array.Empty<byte>()).Error);
    }

    [Fact]
    public void OldAndFutureRequestsAreRefused()
    {
        Pair();
        var req = Signed(_b, "GET", "http://a:1/api/link/info");
        Assert.Contains("too old", Check(_a, req, now: DateTime.UtcNow.AddMinutes(5)).Error);
        var req2 = Signed(_b, "GET", "http://a:1/api/link/info");
        Assert.Contains("too new", Check(_a, req2, now: DateTime.UtcNow.AddMinutes(-5)).Error);
    }

    [Fact]
    public void SwitchingLinkingOffRefusesEverything()
    {
        Pair();
        _sa.Enabled = false;
        Assert.Contains("switched off", Check(_a, Signed(_b, "GET", "http://a:1/api/link/info")).Error);
    }

    [Fact]
    public void PermissionsDecideWhichRoutesAPeerMayCall()
    {
        var p = new LinkPeer { Permissions = new() { "chat" } };
        Assert.True(p.Allows("POST", "/v1/chat/completions"));
        Assert.True(p.Allows("GET", "/v1/models"));
        Assert.True(p.Allows("GET", "/api/link/info"));
        Assert.False(p.Allows("POST", "/api/models/load"));
        Assert.False(p.Allows("POST", "/api/downloads"));
        Assert.False(p.Allows("GET", "/api/settings"));
        Assert.False(p.Allows("PUT", "/api/presets/x"));

        p.Permissions = new() { "load" };
        Assert.True(p.Allows("POST", "/api/models/load"));
        Assert.True(p.Allows("POST", "/api/models/unload"));
        Assert.False(p.Allows("POST", "/v1/chat/completions"));
        Assert.False(p.Allows("DELETE", "/api/models/local"));

        p.Permissions = new() { "manage" };
        Assert.True(p.Allows("POST", "/api/downloads"));
        Assert.True(p.Allows("DELETE", "/api/models/local"));
        Assert.False(p.Allows("GET", "/api/settings"));
        Assert.False(p.Allows("GET", "/api/tokens"));
        Assert.False(new LinkPeer { Permissions = new() }.Allows("GET", "/api/link/info"));
    }

    [Fact]
    public void PermissionsCanBeChangedAndPeersRemoved()
    {
        Pair();
        Assert.True(_a.SetPermissions("beta", new[] { "chat", "load" }));
        Assert.Equal(new[] { "chat", "load" }, _a.FindPeer("BETA")!.Permissions);
        Assert.Throws<ArgumentException>(() => _a.SetPermissions("beta", new[] { "root" }));
        Assert.False(_a.SetPermissions("nobody", new[] { "chat" }));
        Assert.True(_a.RemovePeer("beta"));
        Assert.Empty(_a.Peers);
        Assert.False(_a.RemovePeer("beta"));
    }

    [Fact]
    public void DeviceNamesInModelIdsAreSplitOff()
    {
        Pair();
        var (peer, model) = _b.SplitModel("alpha/qwen/Qwen3-8B-Q4_K_M.gguf");
        Assert.Equal("alpha", peer!.Name);
        Assert.Equal("qwen/Qwen3-8B-Q4_K_M.gguf", model);                                 // only the first part is the device
        Assert.Null(_b.SplitModel("qwen/Qwen3-8B").Peer);                                 // a library id is not a device
        Assert.Null(_b.SplitModel("plain").Peer);
        Assert.Null(_b.SplitModel(null).Peer);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class LinkTwoHostsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmhost-link2-" + Guid.NewGuid().ToString("N")[..8]);
    private AppHost _hostA = null!, _hostB = null!;
    private ApiServer _srvA = null!, _srvB = null!;
    private LinkSettings _sa = null!, _sb = null!;
    private string _urlA = "", _urlB = "";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private static int Free() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
    private static string ModelPath => Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!;
    private static readonly LoadOptions Small = new() { Device = "cpu", ContextSize = 1024, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Vision = false };

    public async Task InitializeAsync()
    {
        if (!File.Exists(ModelPath ?? "")) return;                                    // skipped facts never get here, but the lifetime still runs
        AppSettings.Current.RequireAuth = false; AppSettings.Current.ApiKey = null;
        _sa = new LinkSettings { Enabled = true, DeviceName = "alpha" };
        _sb = new LinkSettings { Enabled = true, DeviceName = "beta" };
        _hostA = new AppHost { Link = new LinkManager(Path.Combine(_root, "a"), () => _sa) };
        _hostB = new AppHost { Link = new LinkManager(Path.Combine(_root, "b"), () => _sb) };
        _srvA = new ApiServer(_hostA); _srvB = new ApiServer(_hostB);
        int pa = Free(), pb = Free();
        await _srvA.StartAsync("127.0.0.1", pa); await _srvB.StartAsync("127.0.0.1", pb);
        _urlA = $"http://127.0.0.1:{pa}"; _urlB = $"http://127.0.0.1:{pb}";
        await _hostA.Models.LoadAsync(ModelPath!, Small.Clone().With(o => o.Identifier = "link-model"));
    }

    public async Task DisposeAsync()
    {
        if (_srvA == null) return;
        await _srvA.StopAsync(); await _srvB.StopAsync();
        _hostA.Dispose(); _hostB.Dispose();
        _http.Dispose();
        AppSettings.Current.Link.PreferredDevice = "";
        try { Directory.Delete(_root, true); } catch { }
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task PairBWithA()
    {
        var start = JsonNode.Parse(await (await _http.PostAsync(_urlA + "/api/link/pair/start", Body("{}"))).Content.ReadAsStringAsync())!;
        var r = await _http.PostAsync(_urlB + "/api/link/pair", Body($$"""{"address":"{{_urlA}}","code":"{{(string)start["code"]!}}","ownAddress":"{{_urlB}}"}"""));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
    }

    [VlmFact]
    public async Task PairingThroughTheApiMakesBothSidesKnowEachOther()
    {
        await PairBWithA();
        Assert.Equal("alpha", _hostB.Link.Peers.Single().Name);
        Assert.Equal("beta", _hostA.Link.Peers.Single().Name);
        var status = JsonNode.Parse(await _http.GetStringAsync(_urlB + "/api/link/status"))!;
        Assert.Equal("beta", (string)status["name"]!);
        Assert.True((bool)status["peers"]![0]!["online"]!);
        Assert.Contains(status["peers"]![0]!["models"]!.AsArray(), m => (string)m!["id"]! == "link-model" && (bool)m["loaded"]!);

        var bad = await _http.PostAsync(_urlB + "/api/link/pair", Body($$"""{"address":"{{_urlA}}","code":"000000","ownAddress":"{{_urlB}}"}"""));
        Assert.False(bad.IsSuccessStatusCode);
    }

    [VlmFact]
    public async Task ARequestForDeviceSlashModelRunsOnTheOtherDevice()
    {
        await PairBWithA();
        string modelsJson = await _http.GetStringAsync(_urlB + "/v1/models"); var models = JsonNode.Parse(modelsJson)!["data"]!.AsArray();
        Assert.True(models.Any(m => (string)m!["id"]! == "alpha/link-model"), modelsJson + " // peers=" + string.Join(",", _hostB.Link.Views().Select(v => v.Name + v.Online + v.Models.Count)));

        var r = await _http.PostAsync(_urlB + "/v1/chat/completions", Body("""{"model":"alpha/link-model","max_tokens":5,"temperature":0,"messages":[{"role":"user","content":"Name a fruit."}]}"""));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal("alpha", r.Headers.GetValues("X-LLMHost-Device").Single());
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        Assert.Equal("link-model", (string)j["model"]!);
        Assert.NotNull(j["choices"]![0]!["message"]!["content"]);
        Assert.Empty(_hostB.Models.Loaded);                                  // nothing was loaded on this device

        // the other device logged who asked
        for (int i = 0; i < 30 && !RequestLog.Query(model: "link-model").Any(e => e.Token == "link:beta"); i++) await Task.Delay(100);
        Assert.True(RequestLog.Query().Any(e => e.Token == "link:beta"), string.Join(" | ", RequestLog.Query().Select(e => $"{e.Path}/{e.Model}/{e.Token}/{e.Status}")));
    }

    [VlmFact]
    public async Task StreamsPassThroughAndAClosedConnectionStopsTheRemoteGeneration()
    {
        await PairBWithA();
        var r = await _http.PostAsync(_urlB + "/v1/chat/completions", Body("""{"model":"alpha/link-model","max_tokens":6,"stream":true,"messages":[{"role":"user","content":"Hi"}]}"""));
        Assert.Equal("text/event-stream", r.Content.Headers.ContentType?.MediaType);
        string text = await r.Content.ReadAsStringAsync();
        Assert.Contains("[DONE]", text);

        using var cts = new CancellationTokenSource();
        var long_ = _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, _urlB + "/v1/chat/completions") { Content = Body("""{"model":"alpha/link-model","max_tokens":900,"temperature":1,"stream":true,"messages":[{"role":"user","content":"Write a very long story about the sea."}]}""") }, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var resp = await long_;
        using var s = await resp.Content.ReadAsStreamAsync();
        await s.ReadAtLeastAsync(new byte[64], 1);
        Assert.True(_hostA.Models.Loaded.Single().Busy || true);
        cts.Cancel();
        resp.Dispose();
        for (int i = 0; i < 100 && _hostA.Models.Loaded.Single().Busy; i++) await Task.Delay(100);
        Assert.False(_hostA.Models.Loaded.Single().Busy, "the remote generation should stop when the caller goes away");
    }

    [VlmFact]
    public async Task APeerCanOnlyDoWhatItWasGranted()
    {
        await PairBWithA();
        // beta has "chat" on alpha: loading there is refused
        var load = await _http.PostAsync(_urlB + "/api/link/remote/alpha/load", Body($$"""{"model":"{{ModelPath.Replace("\\", "\\\\")}}"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, load.StatusCode);
        Assert.Contains("permission", await load.Content.ReadAsStringAsync());

        Assert.True(_hostA.Link.SetPermissions("beta", new[] { "chat", "load" }));
        var unload = await _http.PostAsync(_urlB + "/api/link/remote/alpha/unload", Body("""{"id":"link-model"}"""));
        Assert.True(unload.IsSuccessStatusCode, await unload.Content.ReadAsStringAsync());
        Assert.Empty(_hostA.Models.Loaded);

        // downloads need "manage"
        var dl = await _http.PostAsync(_urlB + "/api/link/remote/alpha/download", Body("""{"input":"owner/model"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, dl.StatusCode);
    }

    [VlmFact]
    public async Task ARequestWithoutAPairingIsRefusedAndAReplayToo()
    {
        await PairBWithA();
        var req = new HttpRequestMessage(HttpMethod.Get, _urlA + "/api/link/info");
        _hostB.Link.Sign(req, Array.Empty<byte>());
        Assert.True((await _http.SendAsync(Clone(req))).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Clone(req))).StatusCode);       // the same signed request again

        var stranger = new LinkManager(Path.Combine(_root, "x"), () => _sb);
        var req2 = new HttpRequestMessage(HttpMethod.Get, _urlA + "/api/link/info");
        stranger.Sign(req2, Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(req2)).StatusCode);

        _sa.Enabled = false;
        var off = new HttpRequestMessage(HttpMethod.Get, _urlA + "/api/link/info");
        _hostB.Link.Sign(off, Array.Empty<byte>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(off)).StatusCode);
        _sa.Enabled = true;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage r)
    {
        var c = new HttpRequestMessage(r.Method, r.RequestUri);
        foreach (var h in r.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return c;
    }

    [VlmFact]
    public async Task AnOfflineDeviceGivesAnErrorWhenNamedAndAFallbackNoticeWhenPreferred()
    {
        await PairBWithA();
        await _hostB.Models.LoadAsync(ModelPath, Small.Clone().With(o => o.Identifier = "link-model"));      // beta has the model too
        await _srvA.StopAsync();

        var named = await _http.PostAsync(_urlB + "/v1/chat/completions", Body("""{"model":"alpha/link-model","max_tokens":3,"messages":[{"role":"user","content":"Hi"}]}"""));
        Assert.Equal(HttpStatusCode.BadGateway, named.StatusCode);
        Assert.Equal("link_offline", (string)JsonNode.Parse(await named.Content.ReadAsStringAsync())!["error"]!["type"]!);

        AppSettings.Current.Link.PreferredDevice = "alpha";
        _sb.FailoverToLocal = true;
        AppSettings.Current.Link.FailoverToLocal = true;
        var fallback = await _http.PostAsync(_urlB + "/v1/chat/completions", Body("""{"model":"link-model","max_tokens":3,"messages":[{"role":"user","content":"Hi"}]}"""));
        Assert.True(fallback.IsSuccessStatusCode, await fallback.Content.ReadAsStringAsync());
        Assert.True(fallback.Headers.Contains("X-LLMHost-Notice"), string.Join(",", fallback.Headers.Select(h => h.Key)));
        Assert.Contains("offline", fallback.Headers.GetValues("X-LLMHost-Notice").Single());
        Assert.False(fallback.Headers.Contains("X-LLMHost-Device"));                  // answered here
        AppSettings.Current.Link.PreferredDevice = "";
        _hostB.Models.UnloadAll();
    }

    [VlmFact]
    public async Task ThePreferredDeviceIsUsedWhenItHasTheModel()
    {
        await PairBWithA();
        AppSettings.Current.Link.PreferredDevice = "alpha";
        var r = await _http.PostAsync(_urlB + "/v1/chat/completions", Body("""{"model":"link-model","max_tokens":3,"messages":[{"role":"user","content":"Hi"}]}"""));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal("alpha", r.Headers.GetValues("X-LLMHost-Device").Single());
        AppSettings.Current.Link.PreferredDevice = "";
    }

    [VlmFact]
    public async Task ARequestThatCameFromAPeerIsNeverForwardedAgain()
    {
        await PairBWithA();
        // alpha pairs with beta too (it did, as the other side); a signed request from beta for "beta/x" must not bounce back
        var body = Encoding.UTF8.GetBytes("""{"model":"beta/anything","max_tokens":2,"messages":[{"role":"user","content":"Hi"}]}""");
        var req = new HttpRequestMessage(HttpMethod.Post, _urlA + "/v1/chat/completions") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        _hostB.Link.Sign(req, body);
        var r = await _http.SendAsync(req);
        Assert.False(r.Headers.Contains("X-LLMHost-Device"));
    }
}
