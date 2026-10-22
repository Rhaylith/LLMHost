using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LLMHostProject.Server;

/// <summary>
/// HTTP server: OpenAI-compatible /v1 API, a management /api used by the web UI, and the embedded web UI itself.
/// </summary>
public partial class ApiServer
{
    private readonly AppHost _host;
    private WebApplication? _app;

    public string? Url { get; private set; }
    public bool Running => _app != null;

    public ApiAuth Auth { get; }
    private readonly RequestRateLimiter _limiter = new();
    private IpAllowList? _allow;
    private string _allowKey = "";
    private System.Security.Cryptography.X509Certificates.X509Certificate2? _cert;
    /// <summary>Address of the HTTPS listener when HTTPS is on.</summary>
    public string? HttpsUrl { get; private set; }

    private bool AllowedClient(System.Net.IPAddress? ip)
    {
        var s = _host.Settings;
        var entries = s.Network.AllowedNetworks is { Count: > 0 } a ? a : IpAllowList.DefaultFor(_bindHost).ToList();
        string key = string.Join(",", entries);
        if (_allow == null || key != _allowKey) { try { _allow = new IpAllowList(entries); } catch (ArgumentException) { _allow = new IpAllowList(new[] { "loopback" }); } _allowKey = key; }
        return _allow.Allows(ip);
    }
    private string _bindHost = "127.0.0.1";

    public ApiServer(AppHost host) { _host = host; Auth = new ApiAuth(host); }

    /// <summary>URL for the embedded window: carries the per-process UI token so the app's own window needs no sign-in.</summary>
    public string? UiUrl => Url == null ? null : Url + "/?k=" + Uri.EscapeDataString(Auth.UiToken);

    public async Task StartAsync(string? bindHost = null, int? port = null)
    {
        if (_app != null) return;
        var s = _host.Settings;
        _bindHost = bindHost ?? s.Host;
        Url = $"http://{_bindHost}:{port ?? s.Port}";
        var tls = s.Network.Tls;
        if (tls.Mode is "selfSigned" or "file")
        {
            _cert = TlsCerts.FromSettings(tls);
            HttpsUrl = $"https://{_bindHost}:{tls.Port}";
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (_cert == null) builder.WebHost.UseUrls(Url);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = Math.Max(s.Network.Limits.MaxBodyMb, s.Network.Limits.MaxUploadMb) * 1024L * 1024;     // the per-route size is applied by the middleware
            if (s.Network.Limits.MaxConnections > 0) k.Limits.MaxConcurrentConnections = s.Network.Limits.MaxConnections;
            if (_cert != null)
            {
                var ip = _bindHost is "localhost" ? System.Net.IPAddress.Loopback : System.Net.IPAddress.TryParse(_bindHost, out var parsed) ? parsed : System.Net.IPAddress.Any;
                k.Listen(ip, port ?? s.Port);
                k.Listen(ip, tls.Port, o => o.UseHttps(_cert));
            }
        });
        if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            Microsoft.Extensions.Hosting.WindowsServiceLifetimeHostBuilderExtensions.UseWindowsService(builder.Host);
        var app = builder.Build();

        // the request log: model routes only (the window's own polling would drown them)
        app.Use(async (ctx, next) =>
        {
            bool logged = ctx.Request.Path.StartsWithSegments("/v1") || ctx.Request.Path.StartsWithSegments("/api/v1") || ctx.Request.Path.StartsWithSegments("/api/v0");
            if (!logged) { await next(); return; }
            var cfg = _host.Settings;
            var entry = new RequestEntry { Method = ctx.Request.Method, Path = ctx.Request.Path.Value ?? "", Ip = ctx.Connection.RemoteIpAddress?.ToString(), Token = Auth.Identity(ctx.Request) };
            RequestLog.Current.Value = entry;
            RequestLog.Capacity = cfg.Logging.RequestLogSize;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Stream? real = null; CaptureStream? capture = null;
            if (cfg.Logging.LogBodies) { real = ctx.Response.Body; capture = new CaptureStream(real); ctx.Response.Body = capture; }
            try { await next(); }
            finally
            {
                entry.Status = ctx.Response.StatusCode;
                entry.DurationMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1);
                if (capture != null) { entry.ResponseBody = RequestLog.Clean(capture.Text()); ctx.Response.Body = real!; }
                RequestLog.Add(entry);
            }
        });

        app.Use(async (ctx, next) =>
        {
            var cfg = _host.Settings;
            // who may connect at all: checked before anything else, with a log line for every refusal
            if (!AllowedClient(ctx.Connection.RemoteIpAddress))
            {
                LogBuffer.Add("server", $"Refused {ctx.Connection.RemoteIpAddress} (not in the allowed networks): {ctx.Request.Method} {ctx.Request.Path}");
                await WriteError(ctx, 403, "This address is not allowed to use the server.", "forbidden");
                return;
            }
            if (cfg.Cors)
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS";
            }
            if (ctx.Request.Method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }

            bool guarded = ctx.Request.Path.StartsWithSegments("/v1") || ctx.Request.Path.StartsWithSegments("/api");
            if (guarded)
            {
                int denied = Auth.Authorize(ctx.Request, out string why);
                if (denied != 0)
                {
                    await WriteError(ctx, denied, why, denied == 401 ? "invalid_api_key" : "insufficient_permissions");
                    return;
                }
                // rate limits: per API token and per client address; the app's own window is exempt
                string bearer = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (bearer != Auth.UiToken)
                {
                    var rl = cfg.Network.Limits;
                    string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                    if (!_limiter.TryAcquire("ip:" + ip, rl.RequestsPerMinutePerIp, out int wait)
                        || (bearer.Length > 0 && !_limiter.TryAcquire("tok:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(bearer)))[..16], rl.RequestsPerMinutePerToken, out wait)))
                    {
                        ctx.Response.Headers["Retry-After"] = wait.ToString();
                        await WriteError(ctx, 429, $"Too many requests; try again in {wait} s.", "rate_limit_exceeded");
                        return;
                    }
                }
            }
            // request size per route class, then the model routes' own checks: idle time (x-llmhost-ttl / "ttl") and the caps on n and max_tokens
            var lim = cfg.Network.Limits;
            bool upload = ctx.Request.Path.StartsWithSegments("/api/documents") || ctx.Request.Path.StartsWithSegments("/api/models/import") || ctx.Request.Path.StartsWithSegments("/v1/audio");
            if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
                sizeFeature.MaxRequestBodySize = (upload ? lim.MaxUploadMb : lim.MaxBodyMb) * 1024L * 1024;
            int? ttl = int.TryParse(ctx.Request.Headers["x-llmhost-ttl"].ToString(), out var th) ? th : null;
            if (ctx.Request.Method == "POST" && (ctx.Request.Path.StartsWithSegments("/v1") || ctx.Request.Path.StartsWithSegments("/api/v1"))
                && (ctx.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false) && ctx.Request.ContentLength is > 0 and <= 8_000_000)
            {
                ctx.Request.EnableBuffering();
                JsonObject? peek = null;
                using (var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8, false, 4096, leaveOpen: true))
                {
                    try { peek = JsonNode.Parse(await sr.ReadToEndAsync()) as JsonObject; } catch { }
                }
                ctx.Request.Body.Position = 0;
                if (peek != null)
                {
                    if (ttl == null && peek["ttl"] is JsonValue tv && tv.TryGetValue<int>(out var tb)) ttl = tb;
                    if (RequestLog.Current.Value is { } le)
                    {
                        le.Model = peek["model"]?.GetValue<string>(); le.Stream = peek["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
                        if (cfg.Logging.LogBodies) le.RequestBody = RequestLog.Clean(peek.ToJsonString());
                    }
                    string? tooMuch = RequestCaps.Check(peek, lim.MaxChoices, lim.MaxTokens);
                    if (tooMuch != null) { await WriteError(ctx, 400, tooMuch, "invalid_request_error"); return; }
                }
            }
            ModelManager.RequestTtl.Value = ttl;
            try { await next(); }
            catch (OperationCanceledException) { }
            catch (Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException bh) when (!ctx.Response.HasStarted)
            {
                await WriteError(ctx, bh.StatusCode, bh.StatusCode == 413 ? $"The request is larger than this server allows ({cfg.Network.Limits.MaxBodyMb} MB; uploads {cfg.Network.Limits.MaxUploadMb} MB)." : bh.Message, bh.StatusCode == 413 ? "request_too_large" : "invalid_request_error");
            }
            catch (GatedModelException g) when (!ctx.Response.HasStarted)
            {
                // structured, so a client can offer "open on Hugging Face" and a retry
                await WriteJson(ctx, new { error = new { message = g.Message, type = "gated", code = "gated", repo = g.Repo, accessUrl = g.AccessUrl } }, 403);
            }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                LogBuffer.Add("server", $"{ctx.Request.Method} {ctx.Request.Path}: {ex.Message}");
                int status = ex is ModelLoadException { Kind: "model_load_blocked" } ? 503 : ex is FileNotFoundException or InvalidOperationException or ArgumentException ? 400 : 500;
                await WriteError(ctx, status, ex.Message, ex is ModelLoadException ml ? ml.Kind : ex is ContextLengthException ? "context_length_exceeded" : "server_error");
            }
        });

        MapUi(app);
        MapOpenAi(app);
        MapAnthropic(app);
        MapResponses(app);
        MapNative(app);
        MapMcp(app);
        MapDocuments(app);
        MapLegacy(app);
        MapManagement(app);

        await app.StartAsync();
        _app = app;
        LogBuffer.Add("server", $"Listening on {Url}");
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app != null) { await app.StopAsync(); await app.DisposeAsync(); }
        LogBuffer.Add("server", "Server stopped");
    }

    // ------------------------------------------------------------------ helpers

    internal static async Task WriteJson(HttpContext ctx, object value, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(value, Json.Compact));
    }

    internal static Task WriteError(HttpContext ctx, int status, string message, string type)
    {
        if (RequestLog.Current.Value is { } cur) cur.Error ??= message;
        if (ctx.Request.Path.StartsWithSegments("/v1/messages"))
        {
            string t = status switch { 401 => "authentication_error", 404 => "not_found_error", >= 500 => "api_error", _ => "invalid_request_error" };
            return WriteJson(ctx, new { type = "error", error = new { type = t, message } }, status);
        }
        return WriteJson(ctx, new { error = new { message, type, code = status } }, status);
    }

    internal static async Task<JsonNode> ReadBody(HttpContext ctx)
    {
        var node = await JsonNode.ParseAsync(ctx.Request.Body);
        return node ?? new JsonObject();
    }

    internal static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static async Task Sse(HttpContext ctx, object chunk)
    {
        await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(chunk, Json.Compact) + "\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    private static void SseHeaders(HttpContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
    }

    // ------------------------------------------------------------------ UI

    private void MapUi(WebApplication app)
    {
        string? html = null;
        // the page's stylesheet and scripts (embedded; plain files, no build step)
        app.MapGet("/ui/{name}", async ctx =>
        {
            string name = (string)ctx.Request.RouteValues["name"]!;
            bool ok = System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z0-9_-]+\.(js|css)$");
            Stream? s = ok ? Assembly.GetExecutingAssembly().GetManifestResourceStream("ui/" + name) : null;
            if (s == null) { ctx.Response.StatusCode = 404; return; }
            using (s)
            {
                ctx.Response.ContentType = name.EndsWith(".css") ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-cache";
                await s.CopyToAsync(ctx.Response.Body);
            }
        });
        app.MapGet("/", async ctx =>
        {
            html ??= LoadEmbedded("index.html");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html);
        });
    }

    private static string LoadEmbedded(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new FileNotFoundException(name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    // ------------------------------------------------------------------ OpenAI compatible

    private object ModelEntry(ModelRuntime r) => new
    {
        id = r.Identifier,
        @object = "model",
        created = new DateTimeOffset(r.LoadedAt).ToUnixTimeSeconds(),
        owned_by = "llmhost",
        state = "loaded",
        context_length = r.ContextSize,
        capabilities = r.Capabilities.ToApi(),
        warnings = r.LoadWarnings,
    };

    private void MapOpenAi(WebApplication app)
    {
        app.MapGet("/v1/models", async ctx =>
        {
            var loaded = _host.Models.Loaded;
            var data = new List<object>(loaded.Select(ModelEntry));
            foreach (var m in _host.Library.List())
            {
                string id = Path.GetFileNameWithoutExtension(m.FileName);
                if (loaded.Any(l => l.Identifier == id)) continue;
                data.Add(new { id, @object = "model", created = 0, owned_by = "local", state = "not-loaded", size = m.SizeBytes, capabilities = m.Capabilities.ToApi() });
            }
            await WriteJson(ctx, new { @object = "list", data });
        });

        app.MapGet("/v1/models/{*id}", async ctx =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            var r = _host.Models.Get(id);
            if (r == null) { await WriteError(ctx, 404, $"Model '{id}' not loaded", "not_found"); return; }
            await WriteJson(ctx, ModelEntry(r));
        });

        app.MapPost("/v1/chat/completions", ChatCompletions);
        app.MapPost("/v1/completions", Completions);
        app.MapPost("/v1/embeddings", Embeddings);
        app.MapPost("/v1/rerank", Rerank);
        app.MapPost("/rerank", Rerank);
        app.MapPost("/v1/reranking", Rerank);
        app.MapPost("/v1/tokenize", async ctx =>
        {
            var body = await ReadBody(ctx);
            var rt = await _host.Models.ResolveAsync(body["model"]?.GetValue<string>(), false, ctx.RequestAborted);
            string text = body["text"]?.GetValue<string>() ?? body["input"]?.GetValue<string>() ?? "";
            await WriteJson(ctx, new { model = rt.Identifier, tokens = rt.CountTokens(text) });
        });
    }

    /// <summary>OpenAI streaming delta for one tool-parser event.</summary>
    private static object? ToolDelta(ToolEvent e) => e switch
    {
        ContentEvent c => new { content = c.Text },
        CallStartEvent s => new { tool_calls = new[] { new { index = s.Index, id = s.Id, type = "function", function = new { name = s.Name, arguments = "" } } } },
        ArgsDeltaEvent a => new { tool_calls = new[] { new { index = a.Index, function = new { arguments = a.Text } } } },
        _ => null,
    };

    private async Task ChatCompletions(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        var rt = await _host.Models.ResolveAsync(body["model"]?.GetValue<string>(), false, ct);
        var g = OpenAiMapping.ReadGeneration(body, _host.GenerationDefaultsFor(rt.Model.Id));
        var messages = OpenAiMapping.ReadMessages(body, rt.ToolFormat);
        if (messages.Count == 0) { await WriteError(ctx, 400, "'messages' is required", "invalid_request_error"); return; }
        var tools = OpenAiMapping.ReadTools(body, rt.ToolFormat);

        // configured default system prompt (an explicit system message wins)
        if (!string.IsNullOrWhiteSpace(g.SystemPrompt) && !messages.Any(m => m.Role == "system"))
            messages.Insert(0, new ChatMessage("system", g.SystemPrompt));
        if (tools != null) OpenAiMapping.ApplyTools(tools, messages, g);

        bool stream = body["stream"]?.GetValue<bool>() ?? false;
        bool includeUsage = body["stream_options"]?["include_usage"]?.GetValue<bool>() ?? false;
        // reasoning_format "none" keeps <think>...</think> inside the content (llama.cpp convention)
        bool inlineReasoning = (body["reasoning_format"] as JsonValue)?.GetValue<string>() == "none";
        int reserve = g.MaxTokens > 0 ? g.MaxTokens : (int)Math.Min(512, rt.ContextSize / 4);
        var prompt = rt.BuildFittingPrompt(messages, reserve, out int dropped, out var images, g.ReasoningEffort, g.ContextOverflow);
        if (dropped > 0) LogBuffer.Add("server", $"Context full: dropped {dropped} oldest message(s)");

        string id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        long created = UnixNow();
        var stats = new GenerationStats();

        // ---- tools offered: parse the output for calls (streamed incrementally)
        if (tools != null)
        {
            var parser = new ToolStreamParser(tools.Format);
            if (stream)
            {
                SseHeaders(ctx);
                await Sse(ctx, Chunk(id, created, rt, new { role = "assistant" }, null));
                async Task Send(IEnumerable<ToolEvent> evs)
                {
                    foreach (var ev in evs)
                        if (ToolDelta(ev) is { } d) await Sse(ctx, Chunk(id, created, rt, d, null));
                }
                await foreach (var seg in rt.StreamSegmentsAsync(prompt, g, stats, ct, images))
                {
                    if (seg.Kind == SegmentKind.Reasoning)
                        await Sse(ctx, Chunk(id, created, rt, inlineReasoning ? new { content = seg.Text } : (object)new { reasoning_content = seg.Text }, null));
                    else await Send(parser.Feed(seg.Text));
                }
                await Send(parser.Finish());
                string fin = parser.HasCalls ? "tool_calls" : stats.FinishReason;
                await Sse(ctx, Chunk(id, created, rt, new { }, fin, null, StatsObj(stats)));
                if (includeUsage) await Sse(ctx, UsageChunk(id, created, rt, Usage(stats)));
                await ctx.Response.WriteAsync("data: [DONE]\n\n");
                return;
            }

            var (text, thinking) = await rt.CompleteSegmentsAsync(prompt, g, stats, ct, images);
            var (answer, calls) = ToolStreamParser.ParseAll(tools.Format, text);
            var toolCalls = calls.Select(c => new { id = c.Id, type = "function", function = new { name = c.Name, arguments = c.Arguments } }).ToList();
            string? reasoningField = thinking.Length > 0 && !inlineReasoning ? thinking : null;
            string contentText = inlineReasoning && thinking.Length > 0 ? "<think>\n" + thinking + "\n</think>\n\n" + answer : answer;
            object msg = toolCalls.Count > 0
                ? new { role = "assistant", content = contentText.Length > 0 ? contentText : null, reasoning_content = reasoningField, tool_calls = toolCalls }
                : new { role = "assistant", content = (string?)contentText, reasoning_content = reasoningField, tool_calls = (object?)null };
            await WriteJson(ctx, new
            {
                id, @object = "chat.completion", created, model = rt.Identifier,
                choices = new[] { new { index = 0, message = msg, finish_reason = toolCalls.Count > 0 ? "tool_calls" : stats.FinishReason } },
                usage = Usage(stats), stats = StatsObj(stats),
            });
            return;
        }

        // ---- plain chat: n choices, optional logprobs, streamed or not
        int nChoices = (int)Math.Clamp(OpenAiMapping.Num(body, "n") ?? 1, 1, 16);
        bool wantLogprobs = g.Logprobs;
        GenerationOptions ForChoice(int i)
        {
            var c = g.Clone();
            if (c.Seed.HasValue) c.Seed = c.Seed.Value + (uint)i;
            return c;
        }

        if (stream)
        {
            SseHeaders(ctx);
            var sseLock = new SemaphoreSlim(1, 1);
            async Task Send(object chunk) { await sseLock.WaitAsync(); try { await Sse(ctx, chunk); } finally { sseLock.Release(); } }
            var all = new GenerationStats[nChoices];

            async Task RunStream(int i)
            {
                var st = all[i] = i == 0 ? stats : new GenerationStats();
                await Send(Chunk(id, created, rt, new { role = "assistant", content = "" }, null, index: i));
                bool open = false;
                int sentLp = 0; var lpTrim = new LeadingTrim();
                await foreach (var seg in rt.StreamSegmentsAsync(prompt, ForChoice(i), st, ct, images))
                {
                    object? lp = null;
                    if (wantLogprobs)
                    {
                        List<TokenLogprob> fresh;
                        lock (st.Logprobs) { fresh = st.Logprobs.Skip(sentLp).ToList(); sentLp += fresh.Count; }
                        lp = LogprobsFormat.Chat(fresh, lpTrim);
                    }
                    if (seg.Kind == SegmentKind.Reasoning && !inlineReasoning)
                        await Send(Chunk(id, created, rt, new { reasoning_content = seg.Text }, null, index: i, logprobs: lp));
                    else
                    {
                        string piece = seg.Text;
                        if (inlineReasoning)
                        {
                            if (seg.Kind == SegmentKind.Reasoning && !open) { open = true; piece = "<think>\n" + piece; }
                            else if (seg.Kind == SegmentKind.Content && open) { open = false; piece = "\n</think>\n\n" + piece; }
                        }
                        await Send(Chunk(id, created, rt, new { content = piece }, null, index: i, logprobs: lp));
                    }
                }
            }
            await Task.WhenAll(Enumerable.Range(0, nChoices).Select(RunStream));
            for (int i = 0; i < nChoices; i++)
                await Send(Chunk(id, created, rt, new { }, all[i].FinishReason, null, i == 0 ? StatsObj(all[0]) : null, index: i));
            if (includeUsage) await Send(UsageChunk(id, created, rt, UsageSum(all)));
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            return;
        }

        var results = new (string Text, string Thinking, GenerationStats Stats)[nChoices];
        await Task.WhenAll(Enumerable.Range(0, nChoices).Select(async i =>
        {
            var st = i == 0 ? stats : new GenerationStats();
            var (t, th) = await rt.CompleteSegmentsAsync(prompt, ForChoice(i), st, ct, images);
            results[i] = (t, th, st);
        }));
        var choices = results.Select((r, i) =>
        {
            object message = r.Thinking.Length == 0
                ? new { role = "assistant", content = r.Text, reasoning_content = (string?)null }
                : inlineReasoning
                    ? new { role = "assistant", content = "<think>\n" + r.Thinking + "\n</think>\n\n" + r.Text, reasoning_content = (string?)null }
                    : new { role = "assistant", content = r.Text, reasoning_content = (string?)r.Thinking };
            return new
            {
                index = i, message, finish_reason = r.Stats.FinishReason,
                logprobs = wantLogprobs ? LogprobsFormat.Chat(r.Stats.Logprobs.ToList()) : null,
            };
        }).ToList();
        await WriteJson(ctx, new
        {
            id, @object = "chat.completion", created, model = rt.Identifier,
            choices, usage = UsageSum(results.Select(r => r.Stats).ToArray()), stats = StatsObj(stats),
        });
    }


    private static object UsageSum(GenerationStats[] all) => new
    {
        prompt_tokens = all[0].PromptTokens,
        completion_tokens = all.Sum(s => s.CompletionTokens),
        total_tokens = all[0].PromptTokens + all.Sum(s => s.CompletionTokens),
        prompt_tokens_details = new { cached_tokens = all[0].CachedTokens },
    };

    private static object Chunk(string id, long created, ModelRuntime rt, object delta, string? finish,
        object? usage = null, object? stats = null, int index = 0, object? logprobs = null) => new
    {
        id, @object = "chat.completion.chunk", created, model = rt.Identifier,
        choices = new[] { new { index, delta, finish_reason = finish, logprobs } },
        usage, stats,
    };

    /// <summary>The extra last chunk of a stream with include_usage: no choices, the usage of the whole request.</summary>
    private static object UsageChunk(string id, long created, ModelRuntime rt, object usage) => new
    {
        id, @object = "chat.completion.chunk", created, model = rt.Identifier, choices = Array.Empty<object>(), usage,
    };

    private static object Usage(GenerationStats s) => new
    {
        prompt_tokens = s.PromptTokens,
        completion_tokens = s.CompletionTokens,
        total_tokens = s.PromptTokens + s.CompletionTokens,
        prompt_tokens_details = new { cached_tokens = s.CachedTokens },
    };

    private static object StatsObj(GenerationStats s) => new
    {
        tokens_per_second = Math.Round(s.TokensPerSecond, 2),
        cached_prompt_tokens = s.CachedTokens,
        time_to_first_token = Math.Round(s.TimeToFirstTokenMs / 1000.0, 3),
        generation_time = Math.Round((s.TotalMs - s.TimeToFirstTokenMs) / 1000.0, 3),
        stop_reason = s.FinishReason,
        completion_tokens = s.CompletionTokens,
        prompt_tokens = s.PromptTokens,
        finish_reason = s.FinishReason,
        draft_tokens = s.DraftTokens,
        draft_accepted = s.DraftAccepted,
    };


    private async Task Completions(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        var rt = await _host.Models.ResolveAsync(body["model"]?.GetValue<string>(), false, ct);
        var g = OpenAiMapping.ReadGeneration(body, _host.GenerationDefaultsFor(rt.Model.Id));
        string prompt = body["prompt"] is JsonArray pa ? string.Join("\n", pa.Select(x => x?.GetValue<string>())) : body["prompt"]?.GetValue<string>() ?? "";
        bool stream = body["stream"]?.GetValue<bool>() ?? false;
        string id = "cmpl-" + Guid.NewGuid().ToString("N")[..24];
        long created = UnixNow();
        int n = (int)Math.Clamp(OpenAiMapping.Num(body, "n") ?? 1, 1, 16);
        bool echo = body["echo"]?.GetValue<bool>() ?? false;
        GenerationOptions ForChoice(int i) { var c = g.Clone(); if (c.Seed.HasValue) c.Seed += (uint)i; return c; }

        if (stream)
        {
            SseHeaders(ctx);
            var sseLock = new SemaphoreSlim(1, 1);
            async Task Send(object chunk) { await sseLock.WaitAsync(); try { await Sse(ctx, chunk); } finally { sseLock.Release(); } }
            var all = new GenerationStats[n];
            await Task.WhenAll(Enumerable.Range(0, n).Select(async i =>
            {
                var st = all[i] = new GenerationStats();
                int sentLp = 0; var lpTrim = new LeadingTrim();
                await foreach (var seg in rt.StreamSegmentsAsync(prompt, ForChoice(i), st, ct))
                {
                    if (seg.Kind == SegmentKind.Reasoning && !g.KeepReasoning) continue;
                    object? lp = null;
                    if (g.Logprobs)
                    {
                        List<TokenLogprob> fresh;
                        lock (st.Logprobs) { fresh = st.Logprobs.Skip(sentLp).ToList(); sentLp += fresh.Count; }
                        lp = LogprobsFormat.Text(fresh, lpTrim);
                    }
                    await Send(new
                    {
                        id, @object = "text_completion", created, model = rt.Identifier,
                        choices = new[] { new { index = i, text = seg.Text, logprobs = lp, finish_reason = (string?)null } },
                    });
                }
            }));
            for (int i = 0; i < n; i++)
                await Send(new
                {
                    id, @object = "text_completion", created, model = rt.Identifier,
                    choices = new[] { new { index = i, text = "", logprobs = (object?)null, finish_reason = (string?)all[i].FinishReason } },
                    usage = i == n - 1 ? UsageSum(all) : null,
                });
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            return;
        }

        var stats = new GenerationStats[n];
        var texts = new string[n];
        await Task.WhenAll(Enumerable.Range(0, n).Select(async i =>
        {
            stats[i] = new GenerationStats();
            texts[i] = await rt.CompleteAsync(prompt, ForChoice(i), stats[i], ct);
        }));
        await WriteJson(ctx, new
        {
            id, @object = "text_completion", created, model = rt.Identifier,
            choices = texts.Select((t, i) => new
            {
                index = i, text = echo ? prompt + t : t, finish_reason = stats[i].FinishReason,
                logprobs = g.Logprobs ? LogprobsFormat.Text(stats[i].Logprobs.ToList()) : null,
            }).ToList(),
            usage = UsageSum(stats), stats = StatsObj(stats[0]),
        });
    }

    /// <summary>The <c>input</c> of an embeddings request: one string or an array of strings.</summary>
    internal static List<string> ReadEmbeddingInputs(JsonNode body)
    {
        var inputs = new List<string>();
        if (body["input"] is JsonArray arr)
        {
            foreach (var x in arr)
            {
                if (x is JsonValue v && v.TryGetValue<string>(out var s)) inputs.Add(s);
                else throw new ArgumentException("'input' must be a string or an array of strings (token arrays are not supported).");
            }
        }
        else if (body["input"] is JsonValue one && one.TryGetValue<string>(out var single)) inputs.Add(single);
        return inputs;
    }

    private async Task Embeddings(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        var rt = await _host.Models.ResolveAsync(body["model"]?.GetValue<string>(), true, ct);
        var inputs = ReadEmbeddingInputs(body);
        if (inputs.Count == 0) { await WriteError(ctx, 400, "'input' is required", "invalid_request_error"); return; }

        int? dims = OpenAiMapping.Num(body, "dimensions") is { } dn ? (int)dn : null;
        if (dims is <= 0) throw new ArgumentException("'dimensions' must be positive.");
        string format = body["encoding_format"]?.GetValue<string>() ?? "float";
        if (format is not ("float" or "base64")) throw new ArgumentException("'encoding_format' must be \"float\" or \"base64\".");
        bool truncate = body["truncate"] is JsonValue tv && tv.TryGetValue<bool>(out var tb) ? tb : true;
        bool? normalize = body["normalize"] is JsonValue nv && nv.TryGetValue<bool>(out var nb) ? nb : null;

        var res = await rt.EmbedManyAsync(inputs, new ModelRuntime.EmbedOptions(truncate, dims, normalize), ct);
        if (dims.HasValue && res.Vectors.Length > 0 && dims > res.Vectors[0].Length)
            throw new ArgumentException($"'dimensions' is {dims} but this model produces {res.Vectors[0].Length}-dimensional vectors.");
        var data = res.Vectors.Select((v, i) => (object)new
        {
            @object = "embedding", index = i,
            embedding = format == "base64" ? (object)EmbeddingMath.ToBase64(v) : v,
        }).ToList();
        int tokens = res.Tokens.Sum();
        await WriteJson(ctx, new
        {
            @object = "list", data, model = rt.Identifier,
            usage = new { prompt_tokens = tokens, total_tokens = tokens },
        });
    }

    /// <summary>The <c>documents</c> of a rerank request: strings or objects with a <c>text</c> field.</summary>
    internal static List<string> ReadRerankDocuments(JsonNode body)
    {
        var docs = new List<string>();
        if (body["documents"] is not JsonArray arr) throw new ArgumentException("'documents' (an array) is required.");
        foreach (var d in arr)
        {
            if (d is JsonValue v && v.TryGetValue<string>(out var s)) docs.Add(s);
            else if (d is JsonObject o && o["text"] is JsonValue tv && tv.TryGetValue<string>(out var t)) docs.Add(t);
            else throw new ArgumentException("Each document must be a string or an object with a \"text\" field.");
        }
        return docs;
    }

    /// <summary>Jina / Cohere style reranking: <c>{model, query, documents, top_n, return_documents}</c> to ordered <c>results</c>.</summary>
    private async Task Rerank(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        string query = body["query"]?.GetValue<string>() ?? throw new ArgumentException("'query' is required.");
        var docs = ReadRerankDocuments(body);
        var rt = await _host.Models.ResolveAsync(body["model"]?.GetValue<string>(), true, ct);
        int topN = OpenAiMapping.Num(body, "top_n") is { } tn && tn > 0 ? (int)Math.Min(tn, docs.Count) : docs.Count;
        bool returnDocs = body["return_documents"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) ? rb : true;
        bool truncate = body["truncate"] is JsonValue tv && tv.TryGetValue<bool>(out var tb) ? tb : true;

        var res = await rt.RerankAsync(query, docs, truncate, ct);
        var results = Enumerable.Range(0, docs.Count).OrderByDescending(i => res.Scores[i]).ThenBy(i => i).Take(topN)
            .Select(i => returnDocs
                ? (object)new { index = i, relevance_score = res.Scores[i], document = new { text = docs[i] } }
                : new { index = i, relevance_score = res.Scores[i] })
            .ToList();
        await WriteJson(ctx, new
        {
            id = "rerank-" + Guid.NewGuid().ToString("N")[..16], model = rt.Identifier, method = res.Method,
            usage = new { prompt_tokens = res.Tokens, total_tokens = res.Tokens }, results,
        });
    }

    // ------------------------------------------------------------------ management API (used by the web UI)

    private static string Q(HttpContext ctx, string key) => ctx.Request.Query[key].ToString();

    private object LocalModelDto(LocalModel m, IReadOnlyList<ModelRuntime> loaded) => new
    {
        m.Id, m.FileName, m.SizeBytes, m.Architecture, m.Name, m.Quantization, m.SizeLabel,
        m.ContextLength, m.IsEmbedding, m.HasChatTemplate,
        supportsVision = m.SupportsVision, capabilities = m.Capabilities.ToApi(), mmproj = m.MmprojPath == null ? null : Path.GetFileName(m.MmprojPath),
        loadedAs = loaded.FirstOrDefault(l => l.Model.Path == m.Path)?.Identifier,
    };

    private static object EstimateDto(LocalModel m, FitEstimate e) => new
    {
        model = m.Id, e.Verdict, e.WeightsBytes, e.KvCacheBytes, e.ComputeBytes, e.ProjectorBytes, e.GpuBytes, e.CpuBytes,
        e.BlockCount, e.OffloadedLayers, e.OutputOnGpu, e.MaxGpuLayers, e.SuggestedContext, e.Moe,
        ramTotalBytes = e.RamTotalBytes, ramAvailableBytes = e.RamAvailableBytes,
        devices = e.Devices.Select(d => new { d.Name, d.IsGpu, needBytes = d.NeedBytes, budgetBytes = d.BudgetBytes, ratio = double.IsFinite(d.Ratio) ? Math.Round(d.Ratio, 3) : (double?)null }),
        warnings = e.Warnings, summary = e.Describe(),
    };

    private static object JobDto(DownloadJob j) => new
    {
        j.Id, j.Repo, j.FileName, state = j.State == DownloadState.NeedsAccess ? "needs-access" : j.State.ToString().ToLowerInvariant(),
        accessUrl = j.State == DownloadState.NeedsAccess ? $"https://huggingface.co/{j.Repo}" : null,
        bytesDone = Interlocked.Read(ref j.BytesDone), j.TotalBytes, progress = j.Progress,
        bytesPerSecond = j.BytesPerSecond, j.Error, j.FinalPath, j.Note, j.Verified, j.Attempt,
    };

    /// <summary>Resolves what the user typed (repo, link, URL, file) into a download job.</summary>
    private async Task<DownloadJob> StartDownloadFromInput(string input, string? quant, bool withMmproj, CancellationToken ct)
    {
        var r = HuggingFace.ParseReference(input);
        if (r.Kind == "url") return _host.Downloads.StartUrl(r.Url!);
        var files = await HuggingFace.ListFilesAsync(r.Repo!, ct);
        RemoteFile? pick = r.Kind == "file"
            ? files.FirstOrDefault(f => f.FileName.Equals(r.File, StringComparison.OrdinalIgnoreCase)
                                        || f.Shards.Any(s => s.Equals(r.File, StringComparison.OrdinalIgnoreCase)))
            : Catalog.PickFile(files, quant ?? r.Quant);
        if (pick == null) throw new FileNotFoundException($"No matching GGUF file in {r.Repo}");
        return _host.Downloads.Start(pick, withMmproj);
    }

    private void MapManagement(WebApplication app)
    {
        // detailed hardware survey: also asks the native backend for its devices (initialises it on first use)
        app.MapGet("/api/hardware", async ctx =>
        {
            var hw = Hardware.Detect();
            var native = GpuDevices.Native();
            var (ramTotal, ramAvail) = Hardware.MemoryStatus();
            await WriteJson(ctx, new
            {
                hw.Cpu, hw.LogicalCores, ramTotalBytes = ramTotal, ramAvailableBytes = ramAvail,
                backend = NativeBackend.Active, systemInfo = NativeBackend.SystemInfo,
                cpuTopology = new
                {
                    logical = CpuTopology.Detect().LogicalCount, physical = CpuTopology.Detect().PhysicalCores, groups = CpuTopology.Detect().Groups,
                    nodes = CpuTopology.Detect().Nodes.Select(n => new { n.Node, cpus = CpuAffinity.Format(n.Cpus), count = n.Cpus.Count }),
                    defaultThreads = Hardware.DefaultThreads, defaultBatchThreads = Hardware.DefaultBatchThreads,
                },
                gpus = hw.Gpus.Select(g =>
                {
                    var d = GpuDevices.For(g);
                    return new
                    {
                        g.Index, g.Name, g.Vendor, g.VramBytes, g.SharedBytes, g.UsableBytes, g.Integrated, g.Driver,
                        llamaIndex = d?.GpuOrdinal, backendName = d?.Name,
                        freeBytes = d != null ? GpuDevices.CurrentFreeBytes(d) : (long?)null,
                    };
                }),
                backendDevices = native.Select(d => new { d.RegistryIndex, d.Name, d.Description, d.Type, d.FreeBytes, d.TotalBytes, d.GpuOrdinal }),
                npus = hw.Npus.Select(n => new { n.Name, n.Vendor, n.Architecture, n.Driver, n.Status }),
            });
        });

        // ---- live load: one reading, or a stream with one reading per second
        // unavailable counters are sent as explicit nulls, so a client always sees the same fields
        var withNulls = new JsonSerializerOptions(Json.Options) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
        object LiveDto(LiveSnapshot s) => JsonSerializer.SerializeToNode(new
        {
            time = s.Time, cpuPercent = s.CpuPercent,
            ramTotalBytes = s.RamTotalBytes, ramAvailableBytes = s.RamAvailableBytes, ramUsedBytes = s.RamTotalBytes - s.RamAvailableBytes,
            process = new { workingSetBytes = s.ProcessWorkingSetBytes, privateBytes = s.ProcessPrivateBytes },
            gpus = s.Gpus.Select(g => new
            {
                g.Index, g.Name, utilization = g.Utilization, dedicatedUsedBytes = g.DedicatedUsedBytes, dedicatedTotalBytes = g.DedicatedTotalBytes,
                sharedUsedBytes = g.SharedUsedBytes, processDedicatedBytes = g.ProcessDedicatedBytes, processSharedBytes = g.ProcessSharedBytes,
                temperatureC = g.TemperatureC,
            }),
            // what each loaded model was expected to need on the GPU / in system memory, next to what the process really holds
            models = _host.Models.Loaded.Select(r => new
            {
                id = r.Identifier, estimatedGpuBytes = r.Estimate?.GpuBytes, estimatedCpuBytes = r.Estimate?.CpuBytes, weightSharers = r.WeightSharers,
            }),
            notes = s.Notes,
        }, withNulls)!;
        app.MapGet("/api/hardware/live", async ctx => await WriteJson(ctx, LiveDto(HardwareMonitor.Shared.Latest())));
        app.MapGet("/api/hardware/stream", async ctx =>
        {
            SseHeaders(ctx);
            var ct = ctx.RequestAborted;
            var queue = System.Threading.Channels.Channel.CreateBounded<LiveSnapshot>(new System.Threading.Channels.BoundedChannelOptions(2)
            { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
            void OnSample(LiveSnapshot s) => queue.Writer.TryWrite(s);
            HardwareMonitor.Shared.Sampled += OnSample;
            using var subscription = HardwareMonitor.Shared.Subscribe();
            try
            {
                await foreach (var s in queue.Reader.ReadAllAsync(ct)) await Sse(ctx, LiveDto(s));
            }
            catch (OperationCanceledException) { }
            finally { HardwareMonitor.Shared.Sampled -= OnSample; }
        });

        // ---- runtime (llama.cpp build) manager
        object RuntimeDto(RuntimeInfo r) => new
        {
            r.Id, r.Name, r.Kind, r.Variant, r.Source, r.Version, r.SizeBytes, r.Compatible, r.Reason, r.Active, r.Selected,
            r.Recommended, r.SourceUrl, path = r.Directory,
        };
        app.MapGet("/api/runtimes", async ctx =>
        {
            var s = RuntimeManager.Survey();
            await WriteJson(ctx, new
            {
                selected = _host.Settings.Runtime, active = NativeBackend.ActiveRuntimeId, autoUpdate = _host.Settings.AutoUpdateRuntimes,
                restartRequired = NativeBackend.IsInitialized && _host.Settings.Runtime != "auto"
                                  && !RuntimeManager.List().Any(r => r.Selected && r.Active),
                survey = new { s.Avx, s.Avx2, s.Avx512, s.Vulkan, gpus = Hardware.Detect().Gpus.Count },
                runtimes = RuntimeManager.List().Select(RuntimeDto),
            });
        });
        app.MapPost("/api/runtimes/select", async ctx =>
        {
            var body = await ReadBody(ctx);
            var r = RuntimeManager.Select(body["id"]?.GetValue<string>() ?? "auto");
            await WriteJson(ctx, new { selected = r?.Id ?? "auto", restartRequired = NativeBackend.IsInitialized });
        });
        app.MapPost("/api/runtimes/install", async ctx =>
        {
            var body = await ReadBody(ctx);
            string src = body["source"]?.GetValue<string>() ?? throw new ArgumentException("'source' is required");
            var r = await RuntimeManager.InstallAsync(src, body["name"]?.GetValue<string>(), ctx.RequestAborted);
            await WriteJson(ctx, RuntimeDto(r));
        });
        app.MapPost("/api/runtimes/update", async ctx =>
            await WriteJson(ctx, new { updated = await RuntimeManager.UpdateAsync(ctx.RequestAborted) }));
        app.MapDelete("/api/runtimes", async ctx =>
            await WriteJson(ctx, new { removed = RuntimeManager.Remove(Q(ctx, "id")) }));

        app.MapGet("/api/status", async ctx =>
        {
            var hw = Hardware.Detect();
            await WriteJson(ctx, new
            {
                version = "1.0",
                backend = NativeBackend.SystemInfo != "" ? NativeBackend.Active : _host.Settings.Backend,
                backendInitialized = NativeBackend.SystemInfo != "",
                systemInfo = NativeBackend.SystemInfo,
                hardware = new
                {
                    hw.Cpu, hw.LogicalCores, physicalCores = CpuTopology.Detect().PhysicalCores, hw.TotalRamBytes,
                    npus = hw.Npus.Select(n => new { n.Name, n.Vendor, n.Architecture, n.Driver, n.Status }),
                    gpus = hw.Gpus.Select(g => new { g.Index, g.Name, g.Vendor, g.VramBytes, g.SharedBytes, g.UsableBytes, g.Integrated, g.Driver }),
                },
                loaded = _host.Models.Loaded.Select(r => new
                {
                    id = r.Identifier, file = r.Model.FileName, modelId = r.Model.Id, weightSharers = r.WeightSharers, r.GpuLayers,
                    device = r.GpuLayers > 0 ? "gpu" : "cpu", vision = r.SupportsVision, capabilities = r.Capabilities.ToApi(), threads = r.Threads, contextSize = r.ContextSize, cachedTokens = r.CachedTokenCount, cacheSlots = r.CacheSlots.Select(s => new { s.Id, s.Tokens, s.Active }), draftModel = r.DraftModelId, draftProposed = r.DraftProposedTotal, draftAccepted = r.DraftAcceptedTotal,
                    sizeBytes = r.Model.SizeBytes, r.Requests, r.Busy, lastUsed = r.LastUsed, loadedBy = r.LoadedBy, ttlSeconds = ModelManager.EffectiveTtlSeconds(r),
                    reasoning = r.ReasoningStyle.ToString().ToLowerInvariant(), supportsReasoning = r.SupportsReasoning,
                    maxConcurrent = r.MaxConcurrent, active = r.ActiveRequests, queued = r.QueuedRequests, toolFormat = r.ToolFormat.ToString().ToLowerInvariant(),
                    warnings = r.LoadWarnings, estimate = r.Estimate == null ? null : new { r.Estimate.Verdict, summary = r.Estimate.Describe() },
                    gpuPlan = r.Plan == null ? null : new { devices = r.Plan.Ordinals, split = r.Plan.TensorSplit, mainGpu = r.Plan.MainGpu, splitMode = r.Plan.SplitMode },
                    isEmbedding = r.Model.IsEmbedding || r.Options.Embeddings,
                }),
                modelsDirectory = _host.Settings.ResolvedModelsDirectory,
            });
        });

        // draft models for speculative decoding: downloaded candidates (same vocabulary, much smaller) and well-known downloads
        app.MapGet("/api/models/drafts", async ctx =>
        {
            var target = _host.Library.Find(Q(ctx, "model")) ?? throw new FileNotFoundException($"No local model matches '{Q(ctx, "model")}'");
            await WriteJson(ctx, new
            {
                candidates = DraftPairing.LocalCandidates(target, _host.Library.List()).Select(m => new { m.Id, m.SizeBytes }),
                suggestions = DraftPairing.Suggestions(target),
            });
        });

        // ---- chat template: what the model file says, and what a template choice would produce
        app.MapGet("/api/models/template", async ctx =>
        {
            var model = _host.Library.Find(Q(ctx, "model")) ?? throw new FileNotFoundException($"No local model matches '{Q(ctx, "model")}'");
            var info = FitEstimator.ReadModel(model.Path).Info;
            var (bos, eos) = PromptTemplates.SpecialTokens(model.Path, info);
            await WriteJson(ctx, new
            {
                model.Id, architecture = info.Architecture, hasTemplate = info.HasChatTemplate, template = info.ChatTemplate, bos, eos,
                reasoning = Reasoning.Detect(info.ChatTemplate).ToString().ToLowerInvariant(),
                toolFormat = ToolFormats.Detect(info.ChatTemplate).ToString().ToLowerInvariant(),
                sample = PromptTemplates.SampleConversation.Select(m => new { role = m.Role, content = m.Content }),
            });
        });

        app.MapPost("/api/models/template/preview", async ctx =>
        {
            var body = await ReadBody(ctx);
            var model = _host.Library.Find(body["model"]?.GetValue<string>() ?? "") ?? throw new FileNotFoundException("The model is not downloaded.");
            var choice = body["template"]?.Deserialize<PromptTemplateSettings>(Json.Options) ?? new PromptTemplateSettings();
            var messages = (body["messages"] as JsonArray)?.Select(m => (m?["role"]?.GetValue<string>() ?? "user", m?["content"]?.GetValue<string>() ?? "")).ToList()
                           ?? PromptTemplates.SampleConversation.ToList();
            bool add = body["addAssistant"]?.GetValue<bool>() ?? true;
            var problems = PromptTemplates.Check(choice).ToList();
            if (problems.Count > 0) { await WriteJson(ctx, new { prompt = (string?)null, error = string.Join(" ", problems) }); return; }
            try
            {
                var (prompt, engine) = PromptTemplates.Preview(model, choice, messages, add);
                await WriteJson(ctx, new { prompt, engine, stopStrings = choice.Mode == PromptTemplateSettings.Manual ? choice.ManualFormat.StopStrings : new List<string>() });
            }
            catch (JinjaException ex) { await WriteJson(ctx, new { prompt = (string?)null, error = ex.Message }); }
        });

        // ---- LoRA adapters
        object AdapterDto(LocalModel a) => new { a.Id, a.FileName, a.SizeBytes, a.Architecture, a.Name };
        app.MapGet("/api/models/adapters", async ctx =>
        {
            string? model = Q(ctx, "model");
            string? arch = string.IsNullOrWhiteSpace(model) ? null : (_host.Library.Find(model) ?? throw new FileNotFoundException($"No local model matches '{model}'")).Architecture;
            var adapters = _host.Library.ListAdapters().Where(a => arch == null || string.IsNullOrEmpty(a.Architecture) || a.Architecture == arch);
            await WriteJson(ctx, adapters.Select(AdapterDto));
        });
        app.MapGet("/api/models/lora", async ctx =>
        {
            var r = _host.Models.Loaded.FirstOrDefault(x => x.Identifier == Q(ctx, "id")) ?? throw new FileNotFoundException($"'{Q(ctx, "id")}' is not loaded");
            await WriteJson(ctx, r.ActiveLoras);
        });
        app.MapPost("/api/models/lora", async ctx =>
        {
            var body = await ReadBody(ctx);
            var r = _host.Models.Loaded.FirstOrDefault(x => x.Identifier == body["id"]?.GetValue<string>())
                    ?? throw new FileNotFoundException($"'{body["id"]}' is not loaded");
            var list = (body["adapters"] as JsonArray)?.Select(a => new LoraSetting
            {
                Id = a?["id"]?.GetValue<string>() ?? a?["path"]?.GetValue<string>() ?? "",
                Scale = a?["scale"] is JsonValue sv && sv.TryGetValue<float>(out var sc) ? sc : 1f,
            }).ToList() ?? new List<LoraSetting>();
            await r.SetLorasAsync(list);
            await WriteJson(ctx, new { adapters = r.ActiveLoras });
        });

        // generation speed at several thread counts (the model must be idle); the setting itself is left unchanged
        app.MapPost("/api/models/benchmark-threads", async ctx =>
        {
            var body = await ReadBody(ctx);
            var r = _host.Models.Loaded.FirstOrDefault(x => x.Identifier == body["id"]?.GetValue<string>())
                    ?? throw new FileNotFoundException($"'{body["id"]}' is not loaded");
            var counts = (body["threads"] as JsonArray)?.Select(x => x!.GetValue<int>()).ToList() ?? ModelRuntime.DefaultBenchmarkCounts(r.Options);
            int tokens = body["tokens"]?.GetValue<int>() ?? 64;
            try
            {
                var rows = await r.BenchmarkThreadsAsync(counts, Math.Clamp(tokens, 8, 512), ctx.RequestAborted);
                var best = rows.OrderByDescending(x => x.TokensPerSecond).FirstOrDefault();
                await WriteJson(ctx, new { rows, best = best?.Threads, current = ModelRuntime.EffectiveThreads(r.Options) });
            }
            catch (InvalidOperationException ex) { await WriteError(ctx, 409, ex.Message, "conflict"); }
        });

        app.MapPost("/api/models/cache/clear", async ctx =>
        {
            var body = await ReadBody(ctx);
            string? id = body["id"]?.GetValue<string>();
            var targets = id == null ? _host.Models.Loaded.ToList() : _host.Models.Loaded.Where(r => r.Identifier == id).ToList();
            if (id != null && targets.Count == 0) { await WriteError(ctx, 404, $"'{id}' is not loaded", "not_found"); return; }
            foreach (var r in targets) await r.ClearCacheAsync();
            await WriteJson(ctx, new { cleared = targets.Count });
        });

        // ---- saved prompt caches (disk)
        ModelRuntime LoadedOrThrow(string? id) =>
            _host.Models.Loaded.FirstOrDefault(r => r.Identifier == id)
            ?? throw new FileNotFoundException($"'{id}' is not loaded");
        app.MapGet("/api/models/cache/saved", async ctx =>
            await WriteJson(ctx, LoadedOrThrow(Q(ctx, "id")).SavedCaches()));
        app.MapPost("/api/models/cache/save", async ctx =>
        {
            var body = await ReadBody(ctx);
            var r = LoadedOrThrow(body["id"]?.GetValue<string>());
            await WriteJson(ctx, await r.SaveCacheAsync(body["name"]?.GetValue<string>() ?? throw new ArgumentException("'name' is required"),
                body["slot"] is JsonValue sv && sv.TryGetValue<int>(out var slot) ? slot : null));
        });
        app.MapPost("/api/models/cache/restore", async ctx =>
        {
            var body = await ReadBody(ctx);
            var r = LoadedOrThrow(body["id"]?.GetValue<string>());
            int tokens = await r.RestoreCacheAsync(body["name"]?.GetValue<string>() ?? throw new ArgumentException("'name' is required"));
            await WriteJson(ctx, new { restored = true, tokens });
        });
        app.MapDelete("/api/models/cache/saved", async ctx =>
        {
            var r = LoadedOrThrow(Q(ctx, "id"));
            await WriteJson(ctx, new { deleted = PromptCacheStore.Delete(r.CacheModel, Q(ctx, "name")) });
        });

        app.MapGet("/api/models/local", async ctx =>
        {
            var loaded = _host.Models.Loaded;
            await WriteJson(ctx, _host.Library.List().Select(m => LocalModelDto(m, loaded)));
        });

        app.MapGet("/api/models/catalog", async ctx =>
            await WriteJson(ctx, new { updated = Catalog.Updated, source = Catalog.Source, url = _host.Settings.CatalogUrl, entries = Catalog.Entries }));

        // fetch the optional remote staff-picks list now (settings.catalogUrl); a bad list is refused and the current one stays
        app.MapPost("/api/models/catalog/refresh", async ctx =>
        {
            string? url = _host.Settings.CatalogUrl;
            if (string.IsNullOrWhiteSpace(url)) { await WriteError(ctx, 400, "No catalog address is set (settings.catalogUrl).", "invalid_request_error"); return; }
            bool changed;
            try { changed = await Catalog.RefreshAsync(url, ctx.RequestAborted); }
            catch (FormatException ex) { await WriteError(ctx, 422, ex.Message, "invalid_catalog"); return; }
            await WriteJson(ctx, new { changed, updated = Catalog.Updated, source = Catalog.Source, count = Catalog.Entries.Count });
        });

        app.MapGet("/api/models/search", async ctx =>
        {
            bool Flag(string n) => Q(ctx, n) is "1" or "true";
            int.TryParse(Q(ctx, "limit"), out var limit);
            var page = await HuggingFace.SearchPageAsync(new HuggingFace.HfSearch(Q(ctx, "q") ?? "", Q(ctx, "sort") ?? "downloads", Q(ctx, "family"), Q(ctx, "pipeline"),
                limit > 0 ? limit : 30, Q(ctx, "cursor"), Flag("vision"), Flag("tools"), Flag("reasoning")), ctx.RequestAborted);
            await WriteJson(ctx, new
            {
                next = page.NextCursor,
                items = page.Items.Select(m => new
                {
                    m.Id, m.Downloads, m.Likes, m.LastModified, m.Pipeline,
                    license = m.Tags?.FirstOrDefault(t => t.StartsWith("license:"))?[8..],
                    capabilities = RemoteCapabilities.FromTags(m.Tags, m.Pipeline, m.Id).ToApi(),
                }),
            });
        });

        // ---- model folders: usage, extra library folders, move / copy of the main folder, stale partial downloads
        object MoveDto(MoveJob? j) => j == null ? new { state = "none" } : new { j.Id, j.From, j.To, j.Mode, j.State, j.TotalBytes, bytesDone = Interlocked.Read(ref j.BytesDone), j.Files, j.FilesDone, j.Current, j.Error, j.Progress };
        app.MapGet("/api/models/disk", async ctx =>
        {
            var r = _host.Directories.Report();
            await WriteJson(ctx, new { main = _host.Settings.ResolvedModelsDirectory, r.Roots, r.Largest, r.Partials, r.TotalModelBytes });
        });
        app.MapPost("/api/models/disk/clean-partials", async ctx =>
        {
            var (files, bytes) = _host.Directories.CleanPartials();
            await WriteJson(ctx, new { deleted = files, bytes });
        });
        app.MapPost("/api/models/directory/add", async ctx =>
        {
            _host.Directories.AddRoot((await ReadBody(ctx))["path"]?.GetValue<string>() ?? "");
            await WriteJson(ctx, new { roots = _host.Settings.ModelRoots });
        });
        app.MapPost("/api/models/directory/remove", async ctx =>
        {
            _host.Directories.RemoveRoot((await ReadBody(ctx))["path"]?.GetValue<string>() ?? "");
            await WriteJson(ctx, new { roots = _host.Settings.ModelRoots });
        });
        app.MapPost("/api/models/directory/move", async ctx =>
        {
            var body = await ReadBody(ctx);
            await WriteJson(ctx, MoveDto(_host.Directories.StartMove(body["to"]?.GetValue<string>() ?? "", body["mode"]?.GetValue<string>() ?? "move")));
        });
        app.MapGet("/api/models/directory/move", async ctx => await WriteJson(ctx, MoveDto(_host.Directories.Last)));
        app.MapDelete("/api/models/directory/move", async ctx => await WriteJson(ctx, new { cancelled = _host.Directories.CancelMove() }));

        // ---- model update check: last results, run a check now, and fetch the newer version of a file
        app.MapGet("/api/models/updates", async ctx => await WriteJson(ctx, new { enabled = _host.Settings.CheckModelUpdates, results = _host.Updates.Last }));
        app.MapPost("/api/models/updates/check", async ctx =>
        {
            var body = await ReadBody(ctx);
            var r = await _host.Updates.CheckAsync(body["id"]?.GetValue<string>(), ctx.RequestAborted);
            await WriteJson(ctx, new { checkedCount = r.Count, updatesAvailable = r.Where(x => x.Status is "update" or "renamed").ToList(), results = r });
        });
        app.MapPost("/api/models/updates/apply", async ctx =>
        {
            var body = await ReadBody(ctx);
            var m = _host.Library.Find(body["id"]?.GetValue<string>() ?? "") ?? throw new FileNotFoundException("Model not found");
            if (_host.Models.Loaded.Any(l => l.Model.Path == m.Path)) { await WriteError(ctx, 400, "Unload the model before updating it", "invalid_request_error"); return; }
            var sc = ModelSidecar.Read(m.Path) ?? throw new InvalidOperationException("The origin of this file is not known, so there is nothing to compare or fetch.");
            var file = (await HuggingFace.ListFilesAsync(sc.Repo, ctx.RequestAborted))
                .FirstOrDefault(f => f.Shards.Contains(sc.FileName, StringComparer.OrdinalIgnoreCase) || f.FileName.Equals(sc.FileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("The file is no longer in the repository.");
            // the old files stay until the new ones are complete and verified, then they are swapped in
            await WriteJson(ctx, JobDto(_host.Downloads.Start(file, includeMmproj: m.MmprojPath != null, replace: true)));
        });

        // card data + README of a Hugging Face repository (cached 24 h)
        app.MapGet("/api/models/card", async ctx =>
        {
            var c = await HuggingFace.GetCardAsync(Q(ctx, "repo"), ctx.RequestAborted, Q(ctx, "refresh") == "1");
            await WriteJson(ctx, new
            {
                c.Repo, c.License, licenseKind = c.LicenseInfo.Kind, licenseWarning = c.LicenseInfo.Warning, c.Tags, c.Pipeline,
                c.Downloads, c.Likes, c.LastModified, c.Gated, c.BaseModel, c.Readme, capabilities = c.Capabilities.ToApi(), fetched = c.FetchedUtc,
            });
        });

        // who a token belongs to (the posted one, or the saved one when none is posted)
        app.MapPost("/api/hf/whoami", async ctx =>
        {
            var body = await ReadBody(ctx);
            var a = await HuggingFace.WhoAmIAsync(body["token"]?.GetValue<string>(), ctx.RequestAborted);
            await WriteJson(ctx, new { a.Valid, a.Name, a.FullName, a.Type, a.Error });
        });

        // architecture table of a downloaded model, read from its GGUF header
        app.MapGet("/api/models/info", async ctx =>
        {
            var m = _host.Library.Find(Q(ctx, "id")) ?? throw new FileNotFoundException("Model not found");
            var g = GgufInfo.Read(m.Path);
            var lic = LicenseInfo.Classify(g.License);
            await WriteJson(ctx, new
            {
                m.Id, g.Architecture, g.Name, g.SizeLabel, g.Quantization, g.ContextLength, g.EmbeddingLength, g.BlockCount, g.HeadCount, g.HeadCountKv,
                g.ExpertCount, g.ExpertUsedCount, g.VocabSize, g.SlidingWindow, g.License, licenseKind = lic.Kind, licenseWarning = lic.Warning,
                g.BaseModel, g.RepoUrl, g.Organization, fileBytes = g.FileLength, capabilities = m.Capabilities.ToApi(),
                chatTemplate = g.HasChatTemplate,
            });
        });

        // files of a repository; optional filters: quant (name, case-insensitive), minSize / maxSize (GB), fits=gpu|ram (approximate, from this machine)
        app.MapGet("/api/models/files", async ctx =>
        {
            var files = (await HuggingFace.ListFilesAsync(Q(ctx, "repo"), ctx.RequestAborted)).AsEnumerable();
            if (Q(ctx, "quant") is { Length: > 0 } qn) files = files.Where(f => f.Quant.Contains(qn, StringComparison.OrdinalIgnoreCase));
            double gb = 1024.0 * 1024 * 1024;
            if (double.TryParse(Q(ctx, "minSize"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lo)) files = files.Where(f => f.TotalDownloadBytes >= lo * gb);
            if (double.TryParse(Q(ctx, "maxSize"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hi)) files = files.Where(f => f.TotalDownloadBytes <= hi * gb);
            var hw = Hardware.Detect();
            long vram = hw.Gpus.Select(g => g.UsableBytes).DefaultIfEmpty(0).Max();
            long ram = Hardware.MemoryStatus().Available;
            bool FitsGpu(RemoteFile f) => vram > 0 && f.TotalDownloadBytes < vram * 0.85;
            bool FitsRam(RemoteFile f) => f.TotalDownloadBytes < ram * 0.85;
            files = Q(ctx, "fits") switch { "gpu" => files.Where(FitsGpu), "ram" => files.Where(FitsRam), _ => files };
            await WriteJson(ctx, files.Select(f => new
            {
                f.Repo, f.FileName, f.SizeBytes, f.Quant, f.IsSplit, f.Shards, vision = f.HasVision, mmproj = f.MmprojFile, mmprojBytes = f.MmprojSize,
                fitsGpu = FitsGpu(f), fitsRam = FitsRam(f),
            }));
        });

        // "will it fit?" estimate for a model + load settings (query values override the model's / global defaults)
        app.MapGet("/api/models/estimate", async ctx =>
        {
            var m = _host.Library.Find(Q(ctx, "id")) ?? throw new FileNotFoundException("Model not found");
            var o = (_host.ModelDefaults.LoadFor(m.Id) ?? _host.Settings.DefaultLoad).Clone();
            if (uint.TryParse(Q(ctx, "ctx"), out var c) && c > 0) o.ContextSize = c;
            if (Q(ctx, "device") is { Length: > 0 } dev) o.Device = dev;
            if (Q(ctx, "gpuLayers") is { Length: > 0 } gl) o.GpuLayers = int.TryParse(gl, out var g) ? g : null;
            if (Q(ctx, "gpuRatio") is { Length: > 0 } gr) o.GpuRatio = float.TryParse(gr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var grv) ? grv : null;
            if (Q(ctx, "kvK") is { Length: > 0 } kk) o.KvCacheTypeK = kk;
            if (Q(ctx, "kvV") is { Length: > 0 } kv) o.KvCacheTypeV = kv;
            if (Q(ctx, "flash") is { Length: > 0 } fl) o.FlashAttentionMode = fl is "1" or "true" or "on" ? "on" : fl is "0" or "false" or "off" ? "off" : "auto";
            if (Q(ctx, "vision") is { Length: > 0 } vi) o.Vision = vi is "1" or "true";
            if (Q(ctx, "ubatch") is { Length: > 0 } ub && uint.TryParse(ub, out var u)) o.UBatchSize = u;
            if (Q(ctx, "devices") is { Length: > 0 } dv) o.GpuDevices = dv.Split(',').Select(x => int.TryParse(x, out var i) ? i : -1).Where(i => i >= 0).ToList();
            if (Q(ctx, "split") is { Length: > 0 } sp) o.TensorSplit = sp.Split(',').Select(x => float.TryParse(x, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0f).ToList();
            if (Q(ctx, "splitMode") is { Length: > 0 } sm) o.SplitMode = sm;
            var e = FitEstimator.Estimate(m, o);
            await WriteJson(ctx, EstimateDto(m, e));
        });

        app.MapPost("/api/models/load", async ctx =>
        {
            var body = await ReadBody(ctx);
            string model = body["model"]?.GetValue<string>() ?? throw new ArgumentException("'model' is required");
            var opts = body["options"]?.Deserialize<LoadOptions>(Json.Options) ?? _host.Settings.DefaultLoad.Clone();
            bool another = body["allowDuplicate"] is JsonValue ad && ad.TryGetValue<bool>(out var adv) && adv;
            var rt = await _host.Models.LoadAsync(model, opts, ctx.RequestAborted, another);
            await WriteJson(ctx, ModelEntry(rt));
        });

        // ---- per-model defaults (load settings + inference settings), the "gear" dialog in My Models
        app.MapGet("/api/models/defaults", async ctx =>
        {
            string id = Q(ctx, "id");
            string key = _host.Library.Find(id)?.Id ?? id;
            var c = _host.ModelDefaults.Get(key);
            await WriteJson(ctx, new
            {
                id = key,
                hasLoad = c?.Load != null, hasGeneration = c?.Generation != null,
                load = c?.Load ?? _host.Settings.DefaultLoad,
                generation = c?.Generation ?? _host.Settings.DefaultGeneration,
            });
        });

        app.MapPut("/api/models/defaults", async ctx =>
        {
            var body = await ReadBody(ctx);
            string id = body["id"]?.GetValue<string>() ?? throw new ArgumentException("'id' is required");
            string key = _host.Library.Find(id)?.Id ?? id;
            var load = body["load"]?.Deserialize<LoadOptions>(Json.Options);
            var gen = body["generation"]?.Deserialize<GenerationOptions>(Json.Options);
            if (load != null) load.Identifier = null;   // the identifier belongs to one load, it is not a default
            _host.ModelDefaults.Set(key, load, gen);
            await WriteJson(ctx, new { ok = true, id = key });
        });

        app.MapDelete("/api/models/defaults", async ctx =>
        {
            string id = Q(ctx, "id");
            string key = _host.Library.Find(id)?.Id ?? id;
            string what = Q(ctx, "what");
            await WriteJson(ctx, new { cleared = _host.ModelDefaults.Clear(key, string.IsNullOrEmpty(what) ? null : what) });
        });

        app.MapPost("/api/models/unload", async ctx =>
        {
            var body = await ReadBody(ctx);
            string? id = body["id"]?.GetValue<string>();
            if (id == null) _host.Models.UnloadAll();
            else if (!_host.Models.Unload(id)) { await WriteError(ctx, 404, $"'{id}' is not loaded", "not_found"); return; }
            await WriteJson(ctx, new { ok = true });
        });

        app.MapDelete("/api/models/local", async ctx =>
        {
            var m = _host.Library.Find(Q(ctx, "id"));
            if (m == null) { await WriteError(ctx, 404, "Model not found", "not_found"); return; }
            if (_host.Models.Loaded.Any(l => l.Model.Path == m.Path))
            { await WriteError(ctx, 400, "Unload the model before deleting it", "invalid_request_error"); return; }
            await WriteJson(ctx, new { deleted = _host.Library.Delete(m) });
        });

        app.MapGet("/api/downloads", async ctx => await WriteJson(ctx, _host.Downloads.Jobs.Select(JobDto)));

        app.MapPost("/api/downloads", async ctx =>
        {
            var body = await ReadBody(ctx);
            string? input = body["input"]?.GetValue<string>() ?? body["url"]?.GetValue<string>();
            if (input != null)
            {
                await WriteJson(ctx, JobDto(await StartDownloadFromInput(input, body["quant"]?.GetValue<string>(),
                    body["mmproj"]?.GetValue<bool>() ?? true, ctx.RequestAborted)));
                return;
            }
            string repo = body["repo"]?.GetValue<string>() ?? throw new ArgumentException("'repo' (or 'input') is required");
            string? file = body["file"]?.GetValue<string>();
            string? quant = body["quant"]?.GetValue<string>();
            var files = await HuggingFace.ListFilesAsync(repo, ctx.RequestAborted);
            var pick = file != null
                ? files.FirstOrDefault(f => f.FileName.Equals(file, StringComparison.OrdinalIgnoreCase))
                : Catalog.PickFile(files, quant);
            if (pick == null) { await WriteError(ctx, 404, "No matching GGUF file in that repository", "not_found"); return; }
            bool withMmproj = body["mmproj"]?.GetValue<bool>() ?? true;
            await WriteJson(ctx, JobDto(_host.Downloads.Start(pick, withMmproj)));
        });

        app.MapPost("/api/downloads/clear", async ctx =>
            await WriteJson(ctx, new { cleared = _host.Downloads.ClearFinished() }));

        app.MapDelete("/api/downloads/{id}", async ctx =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            if (Q(ctx, "remove") == "1")
                await WriteJson(ctx, new { removed = _host.Downloads.Remove(id, Q(ctx, "files") == "1") });
            else
                await WriteJson(ctx, new { cancelled = _host.Downloads.Cancel(id) });
        });
        app.MapPost("/api/downloads/{id}/pause", async ctx =>
            await WriteJson(ctx, new { paused = _host.Downloads.Pause((string)ctx.Request.RouteValues["id"]!) }));
        app.MapPost("/api/downloads/{id}/resume", async ctx =>
            await WriteJson(ctx, new { resumed = _host.Downloads.Resume((string)ctx.Request.RouteValues["id"]!) }));

        // what does this text refer to? (repo / file / direct url)
        app.MapGet("/api/models/resolve", async ctx =>
            await WriteJson(ctx, HuggingFace.ParseReference(Q(ctx, "input"))));

        app.MapPost("/api/models/import", async ctx =>
        {
            var body = await ReadBody(ctx);
            string path = body["path"]?.GetValue<string>() ?? throw new ArgumentException("'path' is required");
            var r = _host.Library.Import(path, body["mode"]?.GetValue<string>() ?? "copy",
                body["publisher"]?.GetValue<string>(), body["name"]?.GetValue<string>(), body["mmproj"]?.GetValue<bool>() ?? true);
            LogBuffer.Add("library", $"Imported {path} as {r.Id}");
            await WriteJson(ctx, new { id = r.Id, path = r.Path, files = r.Files });
        });

        app.MapGet("/api/settings", async ctx => await WriteJson(ctx, _host.Settings));
        app.MapPut("/api/settings", async ctx =>
        {
            var body = await ReadBody(ctx);
            var s = body.Deserialize<AppSettings>(Json.Options) ?? throw new ArgumentException("Invalid settings");
            var errors = SettingsValidator.Validate(s);
            if (errors.Count > 0)
            {
                await WriteJson(ctx, new
                {
                    error = new { message = "Invalid settings: " + SettingsValidator.Summary(errors), type = "validation_error", code = 400 },
                    errors,
                }, 400);
                return;
            }
            _host.Settings.Replace(s);
            _host.Downloads.MaxConcurrent = _host.Settings.MaxConcurrentDownloads;
            await WriteJson(ctx, _host.Settings);
        });

        app.MapGet("/api/presets", async ctx => await WriteJson(ctx, _host.Presets.All()));
        // names, descriptions and a change counter: the page polls the counter to pick up edits made on disk
        app.MapGet("/api/preset-list", async ctx => await WriteJson(ctx, new
        {
            version = _host.Presets.Version,
            presets = _host.Presets.Files().Select(p => new { p.Name, p.Description, hasSystemPrompt = !string.IsNullOrEmpty(p.SystemPrompt) }),
        }));
        app.MapGet("/api/presets/{name}", async ctx =>
        {
            var p = _host.Presets.Get((string)ctx.Request.RouteValues["name"]!);
            if (p == null) await WriteError(ctx, 404, "Preset not found", "not_found"); else await WriteJson(ctx, p);
        });
        app.MapPut("/api/presets/{name}", async ctx =>
        {
            var body = await ReadBody(ctx);
            var g = body.Deserialize<GenerationOptions>(Json.Options) ?? new GenerationOptions();
            string saved = _host.Presets.Save((string)ctx.Request.RouteValues["name"]!, g, body["description"]?.GetValue<string>());
            await WriteJson(ctx, new { ok = true, name = saved });
        });
        // ---- sharing: export as name.preset.json; import from pasted text, a file's text or an https address (preview first, then save)
        app.MapGet("/api/presets/{name}/export", async ctx =>
        {
            var p = _host.Presets.Get((string)ctx.Request.RouteValues["name"]!);
            if (p == null) { await WriteError(ctx, 404, "Preset not found", "not_found"); return; }
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{PresetRules.SafeName(p.Name)}.preset.json\"";
            await ctx.Response.WriteAsync(PresetShare.Export(p));
        });
        async Task<(string Text, string? Fallback)> PresetSource(JsonNode body, CancellationToken ct)
        {
            if (body["text"]?.GetValue<string>() is { Length: > 0 } text) return (text, null);
            string url = body["url"]?.GetValue<string>() ?? throw new ArgumentException("Give either 'text' or 'url'.");
            string fetched = await PresetShare.FetchAsync(url, ct);
            string file = Path.GetFileName(new Uri(PresetShare.RewriteGitHub(url)).AbsolutePath);
            return (fetched, System.Text.RegularExpressions.Regex.Replace(file, @"(\.preset)?\.(json|llmpreset)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }
        app.MapPost("/api/presets/import/preview", async ctx =>
        {
            var body = await ReadBody(ctx);
            var (text, fb) = await PresetSource(body, ctx.RequestAborted);
            await WriteJson(ctx, PresetShare.Preview(text, _host.Presets, fb));
        });
        app.MapPost("/api/presets/import", async ctx =>
        {
            var body = await ReadBody(ctx);
            var (text, fb) = await PresetSource(body, ctx.RequestAborted);
            try { await WriteJson(ctx, new { name = PresetShare.Import(text, _host.Presets, body["name"]?.GetValue<string>(), body["overwrite"]?.GetValue<bool>() ?? false, fb) }); }
            catch (InvalidOperationException ex) { await WriteError(ctx, 409, ex.Message, "conflict"); }
        });
        app.MapPost("/api/presets/{name}/duplicate", async ctx =>
        {
            var body = await ReadBody(ctx);
            string copy = _host.Presets.Duplicate((string)ctx.Request.RouteValues["name"]!, body["name"]?.GetValue<string>());
            await WriteJson(ctx, new { name = copy });
        });
        app.MapDelete("/api/presets/{name}", async ctx =>
            await WriteJson(ctx, new { deleted = _host.Presets.Delete((string)ctx.Request.RouteValues["name"]!) }));

        // ---- chats: list, folders, search, duplicate, rename, move / pin, bulk delete, titles, import / export
        object ChatSummary(Conversation c) => new { c.Id, c.Title, c.Model, c.UpdatedAt, c.CreatedAt, c.FolderId, c.Pinned, c.TitleAuto, count = c.Messages.Count };
        string ChatId(HttpContext ctx)
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            if (!ChatStore.ValidId(id)) throw new ArgumentException("Invalid chat id.");
            return id;
        }
        void DeleteChat(string id)
        {
            var c = _host.Chats.Get(id);
            if (c == null) return;
            foreach (var d in c.Documents) _documents.Delete(d.Id);          // the attachments belong to the chat (a duplicate has its own copies)
            _host.Chats.Delete(id);
        }

        app.MapGet("/api/chats", async ctx => await WriteJson(ctx, _host.Chats.List().Select(ChatSummary)));
        app.MapGet("/api/chats/search", async ctx =>
            await WriteJson(ctx, new { query = Q(ctx, "q"), hits = _host.ChatSearch.Search(Q(ctx, "q") ?? "", int.TryParse(Q(ctx, "limit"), out var lim) ? Math.Clamp(lim, 1, 200) : 50) }));
        app.MapGet("/api/chats/{id}", async ctx =>
        {
            var c = _host.Chats.Get(ChatId(ctx));
            if (c == null) await WriteError(ctx, 404, "Chat not found", "not_found"); else await WriteJson(ctx, c);
        });
        app.MapPut("/api/chats/{id}", async ctx =>
        {
            string id = ChatId(ctx);
            var body = await ReadBody(ctx);
            var c = body.Deserialize<Conversation>(Json.Options) ?? new Conversation();
            c.Id = id;
            // folder, pin and the "title is automatic" flag are changed by their own calls: a save of the messages keeps them
            var old = _host.Chats.Get(id);
            if (old != null)
            {
                c.FolderId = old.FolderId; c.Pinned = old.Pinned; c.CreatedAt = old.CreatedAt;
                c.TitleAuto = old.TitleAuto && c.Title == old.Title;
            }
            // per-chat settings: the page sends the complete form as settings.generation; only what differs from the base the chat follows is kept
            c.Settings = old?.Settings;
            if (body["settings"] is JsonObject bs)
            {
                c.Settings ??= new ChatSettings();
                if (bs.ContainsKey("preset")) c.Settings.Preset = string.IsNullOrEmpty(bs["preset"]?.GetValue<string>()) ? null : bs["preset"]!.GetValue<string>();
                var (preset, modelDefault) = BaseLayers(c.Settings.Preset, c.Model);
                var baseSet = SettingsResolver.Effective(null, preset, modelDefault, _host.Settings.DefaultGeneration).Generation;
                if (bs["generation"] is JsonObject full) c.Settings.Overrides = SettingsResolver.Diff(baseSet, full);
                if (bs["load"] != null) c.Settings.Load = bs["load"].Deserialize<LoadOptions>(Json.Options);
            }
            await WriteJson(ctx, _host.Chats.Save(c));
        });
        app.MapDelete("/api/chats/{id}", async ctx =>
        {
            string id = ChatId(ctx);
            bool existed = _host.Chats.Get(id) != null;
            DeleteChat(id);
            await WriteJson(ctx, new { deleted = existed });
        });
        app.MapPost("/api/chats/bulk-delete", async ctx =>
        {
            var ids = ((await ReadBody(ctx))["ids"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new();
            if (ids.Any(i => !ChatStore.ValidId(i))) throw new ArgumentException("Invalid chat id.");
            int n = 0;
            foreach (var id in ids) if (_host.Chats.Get(id) != null) { DeleteChat(id); n++; }
            await WriteJson(ctx, new { deleted = n });
        });
        app.MapPut("/api/chats/{id}/rename", async ctx =>
        {
            var c = _host.Chats.Get(ChatId(ctx));
            if (c == null) { await WriteError(ctx, 404, "Chat not found", "not_found"); return; }
            string title = ((await ReadBody(ctx))["title"]?.GetValue<string>() ?? "").Trim();
            if (title.Length == 0) throw new ArgumentException("The title must not be empty.");
            c.Title = title.Length > 120 ? title[..120] : title;
            c.TitleAuto = false;                                           // typed by the user: never replaced automatically
            await WriteJson(ctx, ChatSummary(_host.Chats.Save(c)));
        });
        app.MapPut("/api/chats/{id}/meta", async ctx =>
        {
            var c = _host.Chats.Get(ChatId(ctx));
            if (c == null) { await WriteError(ctx, 404, "Chat not found", "not_found"); return; }
            var body = await ReadBody(ctx);
            if (body is JsonObject bo && bo.ContainsKey("folderId"))
            {
                string? f = body["folderId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(f) && _host.ChatFolders.List().All(x => x.Id != f)) throw new ArgumentException("The folder does not exist.");
                c.FolderId = string.IsNullOrEmpty(f) ? null : f;
            }
            if (body["pinned"] is JsonValue p && p.TryGetValue<bool>(out var pin)) c.Pinned = pin;
            _host.Chats.Save(c);
            await WriteJson(ctx, ChatSummary(c));
        });
        app.MapPost("/api/chats/{id}/duplicate", async ctx =>
        {
            var src = _host.Chats.Get(ChatId(ctx));
            if (src == null) { await WriteError(ctx, 404, "Chat not found", "not_found"); return; }
            var copy = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(src, Json.Options), Json.Options)!;
            copy.Id = Guid.NewGuid().ToString("N")[..12];
            copy.Title = src.Title + " (copy)"; copy.TitleAuto = false; copy.Pinned = false; copy.CreatedAt = DateTime.UtcNow;
            // attachments are copied under new ids, so deleting one chat never takes the documents of the other away
            copy.Documents = src.Documents.Select(d => _documents.Clone(d.Id) is { } nd ? new ConversationDocument { Id = nd.Id, Name = d.Name, Chars = d.Chars } : null)
                .Where(d => d != null).Select(d => d!).ToList();
            await WriteJson(ctx, ChatSummary(_host.Chats.Save(copy)));
        });
        app.MapPost("/api/chats/{id}/auto-title", async ctx =>
        {
            var c = _host.Chats.Get(ChatId(ctx));
            if (c == null) { await WriteError(ctx, 404, "Chat not found", "not_found"); return; }
            bool force = Q(ctx, "force") == "1";
            var path = c.ActivePath();
            var user = path.FirstOrDefault(m => m.Role == "user");
            var reply = path.FirstOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
            bool mayRename = c.TitleAuto || c.Title is "New chat" or "";
            if ((!_host.Settings.AutoTitleChats || !mayRename) && !force || user == null || reply == null)
            { await WriteJson(ctx, new { c.Title, source = "unchanged" }); return; }
            string? title = null;
            var rt = _host.Models.Loaded.FirstOrDefault(r => r.Identifier == c.Model && !r.Model.IsEmbedding) ?? _host.Models.Loaded.FirstOrDefault(r => !r.Model.IsEmbedding);
            try { if (rt != null) title = await rt.SuggestTitleAsync(user.Content, reply.Content, ctx.RequestAborted); } catch (Exception ex) when (ex is not OperationCanceledException) { }
            string source = title != null ? "model" : "fallback";
            c.Title = title ?? ChatTransfer.FallbackTitle(user.Content);
            c.TitleAuto = true;
            _host.Chats.Save(c);
            await WriteJson(ctx, new { c.Title, source });
        });
        app.MapGet("/api/chats/{id}/export", async ctx =>
        {
            var c = _host.Chats.Get(ChatId(ctx));
            if (c == null) { await WriteError(ctx, 404, "Chat not found", "not_found"); return; }
            string format = (string.IsNullOrEmpty(Q(ctx, "format")) ? "json" : Q(ctx, "format")!).ToLowerInvariant();
            string safe = new string(c.Title.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
            if (safe.Length == 0) safe = "chat";
            (string text, string type, string ext) = format switch
            {
                "markdown" or "md" => (ChatTransfer.ToMarkdown(c), "text/markdown; charset=utf-8", "md"),
                "text" or "txt" => (ChatTransfer.ToText(c), "text/plain; charset=utf-8", "txt"),
                "json" => (JsonSerializer.Serialize(c, Json.Options), "application/json; charset=utf-8", "json"),
                _ => throw new ArgumentException("format must be json, markdown or text."),
            };
            ctx.Response.ContentType = type;
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{safe}.{ext}\"";
            await ctx.Response.WriteAsync(text);
        });
        // body: { "data": <chat, array of chats or a ChatGPT export, as JSON or as a string> }
        app.MapPost("/api/chats/import", async ctx =>
        {
            var body = await ReadBody(ctx);
            var data = body["data"] ?? throw new ArgumentException("'data' is required.");
            string json = data is JsonValue v && v.TryGetValue<string>(out var s) ? s : data.ToJsonString();
            var imported = ChatTransfer.Import(json);
            foreach (var c in imported) _host.Chats.Save(c);
            await WriteJson(ctx, new { imported = imported.Count, chats = imported.Select(ChatSummary) });
        });

        // ---- how full the context window would be for a conversation (system prompt, messages, the text being typed, tools, attachments)
        app.MapPost("/api/context/usage", async ctx =>
        {
            var body = await ReadBody(ctx);
            string? modelId = body["model"]?.GetValue<string>();
            var rt = _host.Models.Loaded.FirstOrDefault(r => r.Identifier == modelId && !r.Model.IsEmbedding) ?? _host.Models.Loaded.FirstOrDefault(r => !r.Model.IsEmbedding);
            if (rt == null) { await WriteJson(ctx, new { loaded = false }); return; }
            var msgs = new List<ChatMessage>();
            if (body["system"]?.GetValue<string>() is { Length: > 0 } sys) msgs.Add(new ChatMessage("system", sys));
            foreach (var m in body["messages"] as JsonArray ?? new())
                msgs.Add(new ChatMessage(m?["role"]?.GetValue<string>() ?? "user", m?["content"]?.GetValue<string>() ?? ""));
            if (body["input"]?.GetValue<string>() is { Length: > 0 } draft) msgs.Add(new ChatMessage("user", draft));
            int promptTokens = msgs.Count == 0 ? 0 : rt.CountTokens(rt.ApplyChatTemplate(msgs, true));
            int toolTokens = body["tools"] is { } tools ? rt.CountTokens(tools is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : tools.ToJsonString()) : 0;
            // attached documents go into the prompt whole when they fit, otherwise as the best passages: an estimate of ~3.5 characters per token, at most a third of the window
            long docChars = 0;
            foreach (var d in body["documentIds"] as JsonArray ?? new()) docChars += _documents.Get(d?.GetValue<string>() ?? "")?.Text.Length ?? 0;
            int context = (int)rt.ContextSize;
            int docTokens = (int)Math.Min(docChars / 3.5, context / 3.0);
            int reserve = body["maxTokens"]?.GetValue<int>() ?? 0;
            int used = promptTokens + toolTokens + docTokens;
            await WriteJson(ctx, new
            {
                loaded = true, model = rt.Identifier, prompt = promptTokens, tools = toolTokens, documents = docTokens, documentsEstimated = docChars > 0,
                used, context, reserve, remaining = Math.Max(0, context - used - Math.Max(0, reserve)),
                percent = context > 0 ? Math.Round(100.0 * used / context, 1) : 0,
                overflowPolicy = _host.GenerationDefaultsFor(rt.Model.Id).ContextOverflow,
            });
        });

        // ---- keyboard shortcuts: every action with its default and effective keys (the page binds them)
        app.MapGet("/api/shortcuts", async ctx =>
        {
            var eff = LLMHostProject.Core.Shortcuts.Effective(_host.Settings.Shortcuts);
            await WriteJson(ctx, LLMHostProject.Core.Shortcuts.Defaults.Select(a => new { a.Id, a.Label, a.Group, @default = a.Keys, keys = eff.GetValueOrDefault(a.Id, "") }));
        });

        // ---- effective settings of a chat: request override > chat > preset > model default > global (see SettingsResolver)
        string LibraryId(string? model) => model == null ? "" : _host.Models.Loaded.FirstOrDefault(r => r.Identifier == model)?.Model.Id ?? model;
        (GenerationOptions? Preset, GenerationOptions? ModelDefault) BaseLayers(string? presetName, string? model)
        {
            GenerationOptions? preset = !string.IsNullOrEmpty(presetName) && _host.Presets.All().TryGetValue(presetName, out var p) ? p : null;
            return (preset, string.IsNullOrEmpty(model) ? null : _host.ModelDefaults.GenerationFor(LibraryId(model)));
        }
        object EffectiveDto(Conversation? chat, string? presetName, string? model, JsonObject? requestOverride = null)
        {
            var (preset, modelDefault) = BaseLayers(presetName, model);
            var eff = SettingsResolver.Effective(chat?.Settings?.Overrides, preset, modelDefault, _host.Settings.DefaultGeneration, requestOverride);
            var baseOnly = SettingsResolver.Effective(null, preset, modelDefault, _host.Settings.DefaultGeneration);
            return new { preset = presetName, model, baseLayer = eff.BaseLayer, @base = baseOnly.Json, overrides = chat?.Settings?.Overrides ?? new JsonObject(), effective = eff.Json, sources = eff.Sources };
        }
        app.MapGet("/api/effective-settings", async ctx =>
        {
            string? chatId = Q(ctx, "chat");
            var chat = !string.IsNullOrEmpty(chatId) && ChatStore.ValidId(chatId) ? _host.Chats.Get(chatId) : null;
            string? preset = string.IsNullOrEmpty(Q(ctx, "preset")) ? chat?.Settings?.Preset : Q(ctx, "preset");
            string? model = string.IsNullOrEmpty(Q(ctx, "model")) ? chat?.Model : Q(ctx, "model");
            await WriteJson(ctx, EffectiveDto(chat, preset, model));
        });
        app.MapPost("/api/chats/{id}/settings/reset", async ctx =>
        {
            var c = _host.Chats.Get(ChatId(ctx));
            if (c == null) { await WriteError(ctx, 404, "Chat not found", "not_found"); return; }
            if (c.Settings != null) { c.Settings.Overrides = null; if (Q(ctx, "preset") == "1") c.Settings.Preset = null; }
            c.SystemPrompt = null; c.Generation = null;                          // the snapshot is rebuilt by the next save
            _host.Chats.Save(c);
            await WriteJson(ctx, EffectiveDto(c, c.Settings?.Preset, c.Model));
        });
        // body: { target: "preset", name } or { target: "modelDefault" }; stores what applies to this chat right now
        app.MapPost("/api/chats/{id}/settings/save-as", async ctx =>
        {
            var c = _host.Chats.Get(ChatId(ctx));
            if (c == null) { await WriteError(ctx, 404, "Chat not found", "not_found"); return; }
            var body = await ReadBody(ctx);
            var (preset, modelDefault) = BaseLayers(c.Settings?.Preset, c.Model);
            var eff = SettingsResolver.Effective(c.Settings?.Overrides, preset, modelDefault, _host.Settings.DefaultGeneration).Generation;
            string target = body["target"]?.GetValue<string>() ?? "";
            if (target == "preset")
            {
                string name = (body["name"]?.GetValue<string>() ?? "").Trim();
                if (name.Length == 0) throw new ArgumentException("A preset needs a name.");
                _host.Presets.Save(name, eff);
                if (c.Settings != null) { c.Settings.Preset = name; c.Settings.Overrides = null; _host.Chats.Save(c); }       // the chat now follows the new preset unchanged
            }
            else if (target == "modelDefault")
            {
                if (string.IsNullOrEmpty(c.Model)) throw new ArgumentException("This chat has no model.");
                _host.ModelDefaults.Set(LibraryId(c.Model), null, eff);
            }
            else throw new ArgumentException("target must be 'preset' or 'modelDefault'.");
            await WriteJson(ctx, EffectiveDto(c, c.Settings?.Preset, c.Model));
        });

        // the public certificate of the HTTPS listener (PEM), for clients that should trust a self-signed server
        app.MapGet("/api/tls/cert", async ctx =>
        {
            if (_cert == null) { await WriteError(ctx, 404, "HTTPS is not enabled.", "not_found"); return; }
            ctx.Response.ContentType = "application/x-pem-file";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"llmhost.pem\"";
            await ctx.Response.WriteAsync(TlsCerts.ExportPem(_cert));
        });

        // ---- request log (model routes), live feed, and Prometheus metrics
        object RequestDto(RequestEntry e, bool full = false) => full ? e : new { e.Id, e.Time, e.Method, e.Path, e.Status, e.DurationMs, e.Model, e.Token, e.Ip, e.InputTokens, e.OutputTokens, e.Stream, e.Error };
        app.MapGet("/api/requests", async ctx =>
        {
            DateTime? since = DateTime.TryParse(Q(ctx, "since"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var sd) ? sd : null;
            var list = RequestLog.Query(Q(ctx, "model"), Q(ctx, "status"), since, Q(ctx, "q"), int.TryParse(Q(ctx, "limit"), out var lim) ? lim : 200);
            await WriteJson(ctx, new { total = RequestLog.Count, bodiesLogged = _host.Settings.Logging.LogBodies, requests = list.Select(e => RequestDto(e)) });
        });
        app.MapGet("/api/requests/stream", async ctx =>
        {
            SseHeaders(ctx);
            await ctx.Response.WriteAsync(": connected\n\n"); await ctx.Response.Body.FlushAsync();
            var queue = System.Threading.Channels.Channel.CreateBounded<RequestEntry>(new System.Threading.Channels.BoundedChannelOptions(100) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
            void On(RequestEntry e) => queue.Writer.TryWrite(e);
            RequestLog.Added += On;
            try { await foreach (var e in queue.Reader.ReadAllAsync(ctx.RequestAborted)) await Sse(ctx, RequestDto(e)); }
            catch (OperationCanceledException) { }
            finally { RequestLog.Added -= On; }
        });
        app.MapGet("/api/requests/{id}", async ctx =>
        {
            var e = RequestLog.Get((string)ctx.Request.RouteValues["id"]!);
            if (e == null) await WriteError(ctx, 404, "Request not found (it may have left the log)", "not_found"); else await WriteJson(ctx, e);
        });
        app.MapDelete("/api/requests", async ctx => { RequestLog.Clear(); await WriteJson(ctx, new { cleared = true }); });
        app.MapGet("/metrics", async ctx =>
        {
            if (!_host.Settings.MetricsPublic)
            {
                int denied = Auth.Authorize(ctx.Request, out string why);
                if (denied != 0) { await WriteError(ctx, denied, why, denied == 401 ? "invalid_api_key" : "insufficient_permissions"); return; }
            }
            var live = HardwareMonitor.Shared.Latest(TimeSpan.FromSeconds(5));
            ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            await ctx.Response.WriteAsync(Metrics.Render(
                _host.Models.Loaded.Select(r => (r.Identifier, r.ActiveRequests, r.QueuedRequests, r.CachedTokenCount)),
                live.Gpus.Where(g => g.ProcessDedicatedBytes.HasValue).Select(g => (g.Index.ToString(), g.ProcessDedicatedBytes!.Value))));
        });

        // ---- chat folders
        object FolderDto(ChatFolder f) => new { f.Id, f.Name, f.ParentId, f.Order };
        app.MapGet("/api/chat-folders", async ctx => await WriteJson(ctx, _host.ChatFolders.List().Select(FolderDto)));
        app.MapPut("/api/chat-folders/{id}", async ctx =>
        {
            var body = await ReadBody(ctx);
            var f = _host.ChatFolders.Upsert((string)ctx.Request.RouteValues["id"]!, body["name"]?.GetValue<string>() ?? "", body["parentId"]?.GetValue<string>(),
                body["order"] is JsonValue o && o.TryGetValue<int>(out var ord) ? ord : null);
            await WriteJson(ctx, FolderDto(f));
        });
        // chats inside the folder (and its sub-folders) move up to the parent, or are deleted with ?chats=delete
        app.MapDelete("/api/chat-folders/{id}", async ctx =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            var parent = _host.ChatFolders.List().FirstOrDefault(f => f.Id == id)?.ParentId;
            var doomed = _host.ChatFolders.Delete(id);
            int moved = 0, deleted = 0;
            foreach (var c in _host.Chats.List().Where(c => c.FolderId != null && doomed.Contains(c.FolderId)))
            {
                if (Q(ctx, "chats") == "delete") { DeleteChat(c.Id); deleted++; }
                else { c.FolderId = parent; _host.Chats.Save(c); moved++; }
            }
            await WriteJson(ctx, new { folders = doomed.Count, moved, deleted });
        });

        // ---- API tokens
        object TokenDto(ApiToken t) => new { t.Id, t.Name, t.Prefix, t.Permissions, t.CreatedAt, t.LastUsedAt, t.Disabled };
        app.MapGet("/api/tokens", async ctx => await WriteJson(ctx, new
        {
            required = Auth.Required, requireAuth = _host.Settings.RequireAuth, legacyKey = !string.IsNullOrEmpty(_host.Settings.ApiKey),
            permissions = Scopes.All, tokens = Auth.List().Select(TokenDto),
        }));
        app.MapPost("/api/tokens", async ctx =>
        {
            var body = await ReadBody(ctx);
            var perms = (body["permissions"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList();
            var (t, secret) = Auth.Create(body["name"]?.GetValue<string>() ?? "", perms);
            await WriteJson(ctx, new { token = TokenDto(t), secret });     // the secret is returned only here
        });
        app.MapPut("/api/tokens/{id}", async ctx =>
        {
            var body = await ReadBody(ctx);
            var perms = (body["permissions"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList();
            var t = Auth.Update((string)ctx.Request.RouteValues["id"]!, body["name"]?.GetValue<string>(), perms,
                body["disabled"] is JsonValue d && d.TryGetValue<bool>(out var db) ? db : null);
            if (t == null) await WriteError(ctx, 404, "Token not found", "not_found"); else await WriteJson(ctx, TokenDto(t));
        });
        app.MapDelete("/api/tokens/{id}", async ctx =>
            await WriteJson(ctx, new { revoked = Auth.Revoke((string)ctx.Request.RouteValues["id"]!) }));

        app.MapGet("/api/update", async ctx => await WriteJson(ctx, new
        {
            current = LLMHostProject.Desktop.UpdateChecker.CurrentVersion, enabled = _host.Settings.CheckUpdates, feed = _host.Settings.UpdateFeedUrl,
            latest = LLMHostProject.Desktop.UpdateChecker.Latest, lastChecked = LLMHostProject.Desktop.UpdateChecker.LastChecked, error = LLMHostProject.Desktop.UpdateChecker.LastError,
        }));
        app.MapPost("/api/update/check", async ctx =>
        {
            var u = await LLMHostProject.Desktop.UpdateChecker.CheckAsync(ctx.RequestAborted);
            await WriteJson(ctx, new { current = LLMHostProject.Desktop.UpdateChecker.CurrentVersion, latest = u, error = LLMHostProject.Desktop.UpdateChecker.LastError });
        });

        // open PowerShell + Claude Code wired to this server and the chosen (loaded) model
        app.MapPost("/api/launch/claude-code", async ctx =>
        {
            var body = await ReadBody(ctx);
            string? requested = body["model"]?.GetValue<string>();
            var loaded = _host.Models.Loaded.Where(r => !r.Model.IsEmbedding && !r.Options.Embeddings).ToList();
            var rt = !string.IsNullOrWhiteSpace(requested)
                ? loaded.FirstOrDefault(r => r.Identifier.Equals(requested, StringComparison.OrdinalIgnoreCase))
                : loaded.OrderByDescending(r => r.LastUsed).FirstOrDefault();
            if (rt == null) { await WriteError(ctx, 400, "Load a chat model first, then launch Claude Code with it.", "invalid_request_error"); return; }
            // when tokens are required this window's own secret is passed along; otherwise any value works
            string token = Auth.Required ? Auth.UiToken : "local";
            ClaudeCodeLauncher.Launch(Url!, token, rt.Identifier, body["directory"]?.GetValue<string>());
            LogBuffer.Add("server", $"Launched Claude Code with model {rt.Identifier}");
            await WriteJson(ctx, new { ok = true, model = rt.Identifier });
        });

        // write / remove the settings-file entries the Claude Code desktop app (and CLI) read
        app.MapPost("/api/launch/claude-code/configure", async ctx =>
        {
            var body = await ReadBody(ctx);
            string? requested = body["model"]?.GetValue<string>();
            var loaded = _host.Models.Loaded.Where(r => !r.Model.IsEmbedding && !r.Options.Embeddings).ToList();
            var rt = !string.IsNullOrWhiteSpace(requested)
                ? loaded.FirstOrDefault(r => r.Identifier.Equals(requested, StringComparison.OrdinalIgnoreCase))
                : loaded.OrderByDescending(r => r.LastUsed).FirstOrDefault();
            if (rt == null) { await WriteError(ctx, 400, "Load a chat model first.", "invalid_request_error"); return; }
            string token = Auth.Required ? Auth.UiToken : "local";
            string path = ClaudeCodeLauncher.Configure(Url!, token, rt.Identifier, body["directory"]?.GetValue<string>());
            await WriteJson(ctx, new { ok = true, path, model = rt.Identifier, note = Auth.Required ? "authentication is on: the token is only valid until this app restarts" : null });
        });
        app.MapPost("/api/launch/claude-code/unconfigure", async ctx =>
        {
            var body = await ReadBody(ctx);
            string? dir = body["directory"]?.GetValue<string>();
            await WriteJson(ctx, new { removed = ClaudeCodeLauncher.Remove(dir), path = ClaudeCodeLauncher.SettingsPath(dir) });
        });

        app.MapPost("/api/shutdown", async ctx =>
        {
            LogBuffer.Add("server", "Shutdown requested through the API");
            await WriteJson(ctx, new { ok = true });
            _ = Task.Run(async () => { await Task.Delay(200); _host.Shutdown.TrySetResult(); });
        });

        app.MapGet("/api/logs", async ctx =>
        {
            int n = int.TryParse(Q(ctx, "n"), out var v) ? v : 200;
            await WriteJson(ctx, LogBuffer.Tail(n));
        });
    }
}
