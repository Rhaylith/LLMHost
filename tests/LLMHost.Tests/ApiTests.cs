using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Models;
using LLMHostProject.Server;
using Xunit;

namespace LLMHostProject.Tests;

/// <summary>Starts the real HTTP server (no model loaded) on a free port.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public AppHost Host { get; private set; } = null!;
    public ApiServer Server { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        AppSettings.Current.RequireAuth = false;
        AppSettings.Current.ApiKey = null;
        Host = new AppHost();
        Server = new ApiServer(Host);
        await Server.StartAsync("127.0.0.1", port);
        BaseUrl = $"http://127.0.0.1:{port}";
        Http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }

    public async Task DisposeAsync()
    {
        AppSettings.Current.RequireAuth = false;
        await Server.StopAsync();
        Host.Dispose();
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class ApiContractTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ApiContractTests(ServerFixture f) => _f = f;

    private async Task<(HttpStatusCode, JsonNode?)> Get(string path, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer != null) req.Headers.Authorization = new("Bearer", bearer);
        var r = await _f.Http.SendAsync(req);
        string s = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, s.Length > 0 && s[0] is '{' or '[' ? JsonNode.Parse(s) : null);
    }

    private async Task<(HttpStatusCode, JsonNode?)> Post(string path, object? body, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (bearer != null) req.Headers.Authorization = new("Bearer", bearer);
        var r = await _f.Http.SendAsync(req);
        string s = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, s.Length > 0 && s[0] is '{' or '[' ? JsonNode.Parse(s) : null);
    }

    [Fact]
    public async Task ModelListsAreOpenAiShaped()
    {
        var (code, body) = await Get("/v1/models");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("list", body!["object"]!.GetValue<string>());
        Assert.NotNull(body["data"]);
    }

    [Fact]
    public async Task NativeModelListAndStatus()
    {
        var (code, body) = await Get("/api/v1/models");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.NotNull(body!["models"]);
        var (c2, st) = await Get("/api/status");
        Assert.Equal(HttpStatusCode.OK, c2);
        Assert.NotNull(st!["hardware"]);
        Assert.NotNull(st["loaded"]);
    }

    [Fact]
    public async Task ErrorsUseTheDialectOfTheEndpoint()
    {
        var (code, openai) = await Post("/v1/chat/completions", new { messages = Array.Empty<object>() });
        Assert.True((int)code >= 400);
        Assert.NotNull(openai!["error"]!["message"]);

        var (c2, anth) = await Post("/v1/messages", new { model = "x", max_tokens = 5, messages = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, c2);
        Assert.Equal("error", anth!["type"]!.GetValue<string>());
        Assert.Equal("invalid_request_error", anth["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownResponseIs404()
    {
        var (code, body) = await Get("/v1/responses/resp_nope");
        Assert.Equal(HttpStatusCode.NotFound, code);
        Assert.NotNull(body!["error"]);
    }

    [Fact]
    public async Task ModelDefaultsRoundTrip()
    {
        var (put, _) = await _f.Http.PutAsJsonAsync("/api/models/defaults", new
        { id = "pub/model/x.gguf", load = new { contextSize = 8192, device = "gpu" }, generation = new { temperature = 0.25 } })
            .ContinueWith(async t => { var r = await t; return (r.StatusCode, (JsonNode?)null); }).Unwrap();
        Assert.Equal(HttpStatusCode.OK, put);
        var (_, body) = await Get("/api/models/defaults?id=" + Uri.EscapeDataString("pub/model/x.gguf"));
        Assert.True(body!["hasLoad"]!.GetValue<bool>());
        Assert.Equal(8192, body["load"]!["contextSize"]!.GetValue<int>());
        Assert.Equal(0.25, body["generation"]!["temperature"]!.GetValue<double>(), 3);
        var del = await _f.Http.DeleteAsync("/api/models/defaults?id=" + Uri.EscapeDataString("pub/model/x.gguf"));
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    [Fact]
    public async Task DocumentsUploadAndFullContext()
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("The launch code is TANGERINE-42."));
        var (code, up) = await Post("/api/documents", new { name = "note.txt", data = b64 });
        Assert.Equal(HttpStatusCode.OK, code);
        string id = up!["id"]!.GetValue<string>();
        var (_, ctx) = await Post("/api/documents/context", new { ids = new[] { id }, query = "launch code" });
        Assert.Equal("full", ctx!["mode"]!.GetValue<string>());
        Assert.Contains("TANGERINE-42", ctx["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task BadDownloadInputIsRejected()
    {
        var (code, body) = await Post("/api/downloads", new { input = "notamodel" });
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Contains("model reference", body!["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReferenceResolver()
    {
        var (_, r) = await Get("/api/models/resolve?input=" + Uri.EscapeDataString("https://huggingface.co/u/m/resolve/main/f-Q4_K_M.gguf"));
        Assert.Equal("file", r!["kind"]!.GetValue<string>());
        Assert.Equal("u/m", r["repo"]!.GetValue<string>());
        var (_, q) = await Get("/api/models/resolve?input=" + Uri.EscapeDataString("u/m:Q8_0"));
        Assert.Equal("repo", q!["kind"]!.GetValue<string>());
        Assert.Equal("Q8_0", q["quant"]!.GetValue<string>());
    }

    [Fact]
    public async Task TokensAreEnforcedPerPermission()
    {
        // create an admin-free chat token and a models:read token while auth is still off
        var (_, chat) = await Post("/api/tokens", new { name = "chat-only", permissions = new[] { "chat" } });
        var (_, reader) = await Post("/api/tokens", new { name = "reader", permissions = new[] { "models:read" } });
        string chatSecret = chat!["secret"]!.GetValue<string>(), readSecret = reader!["secret"]!.GetValue<string>();
        Assert.StartsWith("ait_", chatSecret);

        AppSettings.Current.RequireAuth = true;
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/v1/models")).Item1);
            Assert.Equal(HttpStatusCode.Forbidden, (await Get("/v1/models", chatSecret)).Item1);
            Assert.Equal(HttpStatusCode.OK, (await Get("/v1/models", readSecret)).Item1);
            Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/tokens", readSecret)).Item1);
            // the page itself stays reachable so a user can sign in
            Assert.Equal(HttpStatusCode.OK, (await _f.Http.GetAsync("/")).StatusCode);
            // the per-process UI token is accepted
            Assert.Equal(HttpStatusCode.OK, (await Get("/api/tokens", _f.Server.Auth.UiToken)).Item1);
        }
        finally { AppSettings.Current.RequireAuth = false; }

        // revoked tokens stop working
        string id = chat["token"]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await _f.Http.DeleteAsync("/api/tokens/" + id)).StatusCode);
        AppSettings.Current.RequireAuth = true;
        try { Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/v1/models", chatSecret)).Item1); }
        finally { AppSettings.Current.RequireAuth = false; }
    }

    [Fact]
    public async Task McpConfigIsValidatedAndListed()
    {
        var put = await _f.Http.PutAsJsonAsync("/api/mcp", new { config = "{\"nope\":1}" });
        Assert.True((int)put.StatusCode >= 400);
        var ok = await _f.Http.PutAsJsonAsync("/api/mcp", new { config = "{\"mcpServers\":{}}" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var (_, list) = await Get("/api/mcp");
        Assert.Empty(list!["servers"]!.AsArray());
    }

    [Fact]
    public async Task RuntimeListNamesTheBundledBuilds()
    {
        var (_, r) = await Get("/api/runtimes");
        var ids = r!["runtimes"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("bundled:vulkan", ids);
        Assert.Contains(ids, i => i.StartsWith("bundled:avx"));
    }
}

[Trait("Category", "Unit")]
public class DownloadTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private readonly byte[] _data;
    private int _requests;
    private volatile bool _abortFirst = true;
    private readonly CancellationTokenSource _stop = new();

    public DownloadTests()
    {
        _data = new byte[3 * 1024 * 1024 + 123];
        new Random(7).NextBytes(_data);
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        _url = $"http://127.0.0.1:{port}/model.gguf";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); } catch { return; }
            Interlocked.Increment(ref _requests);
            _ = Task.Run(async () =>
            {
                try
                {
                    long from = 0;
                    string? range = c.Request.Headers["Range"];
                    if (range != null && range.StartsWith("bytes=")) from = long.Parse(range[6..].Split('-')[0]);
                    c.Response.StatusCode = from > 0 ? 206 : 200;
                    long remaining = _data.Length - from;
                    c.Response.ContentLength64 = remaining;
                    if (from > 0) c.Response.Headers["Content-Range"] = $"bytes {from}-{_data.Length - 1}/{_data.Length}";
                    // the first attempt dies half way through the body (connection reset)
                    long send = _abortFirst && from == 0 ? 1024 * 1024 : remaining;
                    await c.Response.OutputStream.WriteAsync(_data.AsMemory((int)from, (int)send));
                    if (_abortFirst && from == 0) { _abortFirst = false; c.Response.Abort(); return; }
                    c.Response.Close();
                }
                catch { try { c.Response.Abort(); } catch { } }
            });
        }
    }

    public void Dispose() { _stop.Cancel(); try { _listener.Stop(); } catch { } }

    private static async Task<DownloadJob> Wait(DownloadManager m, DownloadJob j)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (j.State is DownloadState.Queued or DownloadState.Downloading && DateTime.UtcNow < deadline) await Task.Delay(100);
        return j;
    }

    [Fact]
    public async Task ResumesAfterAConnectionDropAndVerifiesTheDigest()
    {
        var m = new DownloadManager(Path.Combine(TestEnvironment.Home, "dl-resume.json"));
        string sha = Convert.ToHexString(SHA256.HashData(_data)).ToLowerInvariant();
        var job = m.StartUrl(_url, "testpub", sha);
        await Wait(m, job);
        Assert.Equal(DownloadState.Completed, job.State);
        Assert.True(job.Verified);
        Assert.True(Volatile.Read(ref _requests) >= 2, $"the second request must be a ranged resume (requests={_requests}, attempt={job.Attempt}, size={new FileInfo(job.FinalPath!).Length}/{_data.Length}, path={job.FinalPath}, written={File.GetLastWriteTime(job.FinalPath!):HH:mm:ss.fff} now={DateTime.Now:HH:mm:ss.fff})");
        Assert.Equal(_data, await File.ReadAllBytesAsync(job.FinalPath!));
    }

    [Fact]
    public async Task WrongDigestFailsAndRemovesTheFile()
    {
        _abortFirst = false;
        var m = new DownloadManager(Path.Combine(TestEnvironment.Home, "dl-bad.json"));
        var job = m.StartUrl(_url, "testpub-bad", new string('0', 64));
        await Wait(m, job);
        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Contains("SHA-256 mismatch", job.Error);
        Assert.False(File.Exists(Path.Combine(job.DestinationDirectory, "model.gguf")));
    }

    [Fact]
    public async Task JobsSurviveARestartPaused()
    {
        _abortFirst = false;
        string state = Path.Combine(TestEnvironment.Home, "dl-persist.json");
        var m = new DownloadManager(state);
        var job = m.StartUrl(_url, "testpub-persist");
        await Wait(m, job);
        Assert.Equal(DownloadState.Completed, job.State);

        var again = new DownloadManager(state);
        var restored = Assert.Single(again.Jobs);
        Assert.Equal(DownloadState.Completed, restored.State);
        Assert.Equal(job.Id, restored.Id);
    }
}

[Trait("Category", "Unit")]
public class ParseReferenceTests
{
    [Theory]
    [InlineData("user/model", "repo", "user/model", null)]
    [InlineData("user/model:Q4_K_M", "repo", "user/model", "Q4_K_M")]
    [InlineData("https://huggingface.co/user/model", "repo", "user/model", null)]
    [InlineData("hf.co/user/model/tree/main", "repo", "user/model", null)]
    public void Repositories(string input, string kind, string repo, string? quant)
    {
        var r = HuggingFace.ParseReference(input);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(repo, r.Repo);
        Assert.Equal(quant, r.Quant);
    }

    [Fact]
    public void FilesAndUrls()
    {
        var f = HuggingFace.ParseReference("user/model/sub/dir/m-Q4_K_M.gguf");
        Assert.Equal("file", f.Kind);
        Assert.Equal("sub/dir/m-Q4_K_M.gguf", f.File);
        var u = HuggingFace.ParseReference("https://example.com/files/m.gguf");
        Assert.Equal("url", u.Kind);
        Assert.Throws<ArgumentException>(() => HuggingFace.ParseReference("https://example.com/page"));
        Assert.Throws<ArgumentException>(() => HuggingFace.ParseReference("justaword"));
    }
}
