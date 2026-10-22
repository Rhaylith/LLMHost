using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
[Collection("Settings")]
public class RequestLogUnitTests : IDisposable
{
    public RequestLogUnitTests() { RequestLog.Clear(); RequestLog.Capacity = 2000; Metrics.Reset(); }
    public void Dispose() { RequestLog.Clear(); RequestLog.Capacity = 2000; Metrics.Reset(); }

    private static RequestEntry E(string path = "/v1/chat/completions", int status = 200, string? model = "m1", double ms = 100, int inTok = 5, int outTok = 7)
    {
        var e = new RequestEntry { Method = "POST", Path = path, Status = status, Model = model, DurationMs = ms };
        e.AddTokens(inTok, outTok);
        return e;
    }

    [Fact]
    public void TheRingKeepsTheNewestAndQueriesNewestFirst()
    {
        RequestLog.Capacity = 10;
        for (int i = 0; i < 25; i++) RequestLog.Add(E(path: "/v1/r" + i));
        Assert.Equal(10, RequestLog.Count);
        var all = RequestLog.Query();
        Assert.Equal("/v1/r24", all[0].Path);
        Assert.Equal("/v1/r15", all[^1].Path);
    }

    [Fact]
    public void FiltersCombine()
    {
        RequestLog.Add(E(model: "alpha", status: 200));
        RequestLog.Add(E(model: "beta", status: 404));
        RequestLog.Add(E(model: "beta", status: 503));
        RequestLog.Add(new RequestEntry { Method = "POST", Path = "/v1/embeddings", Status = 200, Error = "boom here" });
        Assert.Single(RequestLog.Query(model: "alp"));
        Assert.Equal(2, RequestLog.Query(model: "beta").Count);
        Assert.Single(RequestLog.Query(status: "404"));
        Assert.Equal(2, RequestLog.Query(status: "error").Count);
        Assert.Equal(2, RequestLog.Query(status: "4xx").Count + RequestLog.Query(status: "5xx").Count);
        Assert.Single(RequestLog.Query(text: "BOOM"));
        Assert.Single(RequestLog.Query(model: "beta", status: "5xx"));
        Assert.Empty(RequestLog.Query(since: DateTime.Now.AddMinutes(1)));
        Assert.Equal(2, RequestLog.Query(limit: 2).Count);
    }

    [Theory]
    [InlineData(200, "200", true)]
    [InlineData(200, "2xx", true)]
    [InlineData(404, "4xx", true)]
    [InlineData(404, "error", true)]
    [InlineData(200, "error", false)]
    [InlineData(500, "4xx", false)]
    [InlineData(200, "abc", false)]
    public void StatusFilters(int status, string filter, bool expected) => Assert.Equal(expected, RequestLog.StatusMatches(status, filter));

    [Fact]
    public void BodiesAreRedactedAndCut()
    {
        string clean = RequestLog.Clean("Authorization: Bearer abcdefghijklmnop12345 and hf_ABCDEFGHIJKLMNOPQRSTUV");
        Assert.DoesNotContain("abcdefghijklmnop12345", clean);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUV", clean);
        string big = RequestLog.Clean(new string('x', 20_000));
        Assert.True(big.Length < 8300);
        Assert.Contains("more characters", big);
        Assert.Equal("", RequestLog.Clean(null));
    }

    [Theory]
    [InlineData("/api/chats/abc123", "/api/chats/{id}")]
    [InlineData("/api/presets/Creative", "/api/presets/{id}")]
    [InlineData("/v1/models/some-model", "/v1/models/{id}")]
    [InlineData("/v1/chat/completions", "/v1/chat/completions")]
    public void RoutesLoseTheirIds(string path, string route) => Assert.Equal(route, RequestLog.Route(path));

    [Fact]
    public void TokensAddUpAcrossSeveralGenerations()
    {
        var e = new RequestEntry();
        e.AddTokens(10, 3); e.AddTokens(4, 6);
        Assert.Equal(14, e.InputTokens);
        Assert.Equal(9, e.OutputTokens);
    }

    [Fact]
    public void TheMetricsAreValidPrometheusText()
    {
        RequestLog.Add(E(status: 200, ms: 30, outTok: 20));
        RequestLog.Add(E(status: 200, ms: 3000, outTok: 10));
        RequestLog.Add(E(status: 429, model: null, ms: 1));
        string text = Metrics.Render(new[] { ("m1", 1, 2, 300) }, new[] { ("0", 123456L) });
        Assert.Contains("llmhost_requests_total{route=\"/v1/chat/completions\",status=\"200\"} 2", text);
        Assert.Contains("llmhost_requests_total{route=\"/v1/chat/completions\",status=\"429\"} 1", text);
        Assert.Contains("llmhost_tokens_generated_total{model=\"m1\"} 30", text);
        Assert.Contains("llmhost_prompt_tokens_total{model=\"m1\"} 10", text);
        Assert.Contains("llmhost_loaded_models 1", text);
        Assert.Contains("llmhost_active_requests{model=\"m1\"} 1", text);
        Assert.Contains("llmhost_queue_depth{model=\"m1\"} 2", text);
        Assert.Contains("llmhost_kv_cache_tokens{model=\"m1\"} 300", text);
        Assert.Contains("llmhost_gpu_memory_bytes{gpu=\"0\"} 123456", text);
        // histogram: cumulative buckets, +Inf equals the count
        Assert.Contains("llmhost_request_duration_seconds_bucket{route=\"/v1/chat/completions\",le=\"0.05\"} 2", text);       // 30 ms and 1 ms
        Assert.Contains("llmhost_request_duration_seconds_bucket{route=\"/v1/chat/completions\",le=\"5\"} 3", text);
        Assert.Contains("llmhost_request_duration_seconds_bucket{route=\"/v1/chat/completions\",le=\"+Inf\"} 3", text);
        Assert.Contains("llmhost_request_duration_seconds_count{route=\"/v1/chat/completions\"} 3", text);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(line.StartsWith("# ") || System.Text.RegularExpressions.Regex.IsMatch(line, @"^[a-z_]+(\{[^}]*\})? [-0-9.e+]+$"), line);
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class RequestLogApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public RequestLogApiTests(ServerFixture f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private async Task<JsonNode> Json(string url) => JsonNode.Parse(await _f.Http.GetStringAsync(url))!;

    [Fact]
    public async Task ModelRoutesAreLoggedWithModelStatusAndErrorAndFiltersWork()
    {
        RequestLog.Clear();
        string model = "log-" + Guid.NewGuid().ToString("N")[..6];
        AppSettings.Current.AutoLoadOnRequest = false;
        var r = await _f.Http.PostAsync("/v1/chat/completions", Body($$"""{"model":"{{model}}","stream":true,"messages":[{"role":"user","content":"hi"}]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        await _f.Http.GetAsync("/api/status");                                   // not a model route: not logged

        var list = await Json("/api/requests");
        var reqs = list["requests"]!.AsArray();
        Assert.Single(reqs);
        var e = reqs[0]!;
        Assert.Equal("/v1/chat/completions", (string)e["path"]!);
        Assert.Equal(400, (int)e["status"]!);
        Assert.Equal(model, (string)e["model"]!);
        Assert.True((bool)e["stream"]!);
        Assert.Contains("not loaded", (string)e["error"]!);
        Assert.True((double)e["durationMs"]! >= 0);
        Assert.Null(e["requestBody"]);                                            // bodies are off by default

        Assert.Single((await Json($"/api/requests?model={model}&status=4xx"))["requests"]!.AsArray());
        Assert.Empty((await Json("/api/requests?status=200"))["requests"]!.AsArray());
        string id = (string)e["id"]!;
        Assert.Equal(model, (string)(await Json("/api/requests/" + id))["model"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Http.GetAsync("/api/requests/nothing")).StatusCode);

        await _f.Http.DeleteAsync("/api/requests");
        Assert.Empty((await Json("/api/requests"))["requests"]!.AsArray());
        AppSettings.Current.AutoLoadOnRequest = true;
    }

    [Fact]
    public async Task BodiesAreKeptRedactedOnlyWhenSwitchedOn()
    {
        RequestLog.Clear();
        var old = AppSettings.Current.Logging.LogBodies;
        try
        {
            AppSettings.Current.Logging.LogBodies = true;
            AppSettings.Current.AutoLoadOnRequest = false;
            await _f.Http.PostAsync("/v1/chat/completions", Body("""{"model":"nobody","messages":[{"role":"user","content":"my secret: hf_ABCDEFGHIJKLMNOPQRSTUVWX"}]}"""));
            var id = (string)(await Json("/api/requests"))["requests"]![0]!["id"]!;
            var full = await Json("/api/requests/" + id);
            Assert.Contains("secret", (string)full["requestBody"]!);
            Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWX", (string)full["requestBody"]!);
            Assert.Contains("not loaded", (string)full["responseBody"]!);
        }
        finally { AppSettings.Current.Logging.LogBodies = old; AppSettings.Current.AutoLoadOnRequest = true; }
    }

    [Fact]
    public async Task TheMetricsEndpointNeedsAuthenticationUnlessMadePublic()
    {
        var r = await _f.Http.GetAsync("/metrics");
        Assert.True(r.IsSuccessStatusCode);                                        // the fixture runs without authentication
        Assert.StartsWith("text/plain", r.Content.Headers.ContentType!.ToString());
        Assert.Contains("llmhost_loaded_models", await r.Content.ReadAsStringAsync());

        AppSettings.Current.RequireAuth = true;
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.Http.GetAsync("/metrics")).StatusCode);
            AppSettings.Current.MetricsPublic = true;
            Assert.True((await _f.Http.GetAsync("/metrics")).IsSuccessStatusCode);
        }
        finally { AppSettings.Current.RequireAuth = false; AppSettings.Current.MetricsPublic = false; }
    }

    [Fact]
    public async Task TheLiveFeedSendsNewRequests()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var resp = await _f.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/requests/stream"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        AppSettings.Current.AutoLoadOnRequest = false;
        _ = _f.Http.PostAsync("/v1/chat/completions", Body("""{"model":"live-feed","messages":[{"role":"user","content":"x"}]}"""));
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null && !line.StartsWith("data: ")) { }
        AppSettings.Current.AutoLoadOnRequest = true;
        Assert.Equal("live-feed", (string)JsonNode.Parse(line![6..])!["model"]!);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class RequestLogIntegrationTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public RequestLogIntegrationTests(ServerFixture f) => _f = f;

    [VlmFact]
    public async Task ARealCompletionCountsItsTokensAndShowsInTheMetrics()
    {
        RequestLog.Clear(); Metrics.Reset();
        var rt = _f.Host.Models.Loaded.FirstOrDefault(r => r.Identifier == "rl-model")
                 ?? await _f.Host.Models.LoadAsync(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!, new LoadOptions { Device = "cpu", ContextSize = 1024, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Vision = false, Identifier = "rl-model" });
        var r = await _f.Http.PostAsync("/v1/chat/completions", new StringContent("""{"model":"rl-model","max_tokens":6,"temperature":0,"messages":[{"role":"user","content":"Name a fruit."}]}""", Encoding.UTF8, "application/json"));
        Assert.True(r.IsSuccessStatusCode);
        await Task.Delay(300);
        var e = RequestLog.Query(model: "rl-model").First();
        Assert.Equal(200, e.Status);
        Assert.True(e.InputTokens > 0, "prompt tokens");
        Assert.Equal(6, e.OutputTokens);
        string metrics = await _f.Http.GetStringAsync("/metrics");
        Assert.Contains("llmhost_tokens_generated_total{model=\"rl-model\"} 6", metrics);
        Assert.Contains("llmhost_loaded_models", metrics);
        Assert.Contains("llmhost_kv_cache_tokens{model=\"rl-model\"}", metrics);
        _f.Host.Models.UnloadAll();
    }
}
