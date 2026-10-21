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

    public ApiServer(AppHost host) { _host = host; Auth = new ApiAuth(host); }

    /// <summary>URL for the embedded window: carries the per-process UI token so the app's own window needs no sign-in.</summary>
    public string? UiUrl => Url == null ? null : Url + "/?k=" + Uri.EscapeDataString(Auth.UiToken);

    public async Task StartAsync(string? bindHost = null, int? port = null)
    {
        if (_app != null) return;
        var s = _host.Settings;
        Url = $"http://{bindHost ?? s.Host}:{port ?? s.Port}";

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(Url);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 256L * 1024 * 1024);
        if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            Microsoft.Extensions.Hosting.WindowsServiceLifetimeHostBuilderExtensions.UseWindowsService(builder.Host);
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            var cfg = _host.Settings;
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
            }
            try { await next(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                LogBuffer.Add("server", $"{ctx.Request.Method} {ctx.Request.Path}: {ex.Message}");
                int status = ex is FileNotFoundException or InvalidOperationException or ArgumentException ? 400 : 500;
                await WriteError(ctx, status, ex.Message, ex is ModelLoadException ml ? ml.Kind : "server_error");
            }
        });

        MapUi(app);
        MapOpenAi(app);
        MapAnthropic(app);
        MapResponses(app);
        MapNative(app);
        MapMcp(app);
        MapDocuments(app);
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
        var prompt = rt.BuildFittingPrompt(messages, reserve, out int dropped, out var images, g.ReasoningEffort);
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
                await Sse(ctx, Chunk(id, created, rt, new { }, fin, includeUsage ? Usage(stats) : null, StatsObj(stats)));
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
                int sentLp = 0;
                await foreach (var seg in rt.StreamSegmentsAsync(prompt, ForChoice(i), st, ct, images))
                {
                    object? lp = null;
                    if (wantLogprobs)
                    {
                        List<TokenLogprob> fresh;
                        lock (st.Logprobs) { fresh = st.Logprobs.Skip(sentLp).ToList(); sentLp += fresh.Count; }
                        lp = ChatLogprobs(fresh);
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
                await Send(Chunk(id, created, rt, new { }, all[i].FinishReason,
                    i == nChoices - 1 && includeUsage ? UsageSum(all) : null, i == 0 ? StatsObj(all[0]) : null, index: i));
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
                logprobs = wantLogprobs ? ChatLogprobs(r.Stats.Logprobs.ToList()) : null,
            };
        }).ToList();
        await WriteJson(ctx, new
        {
            id, @object = "chat.completion", created, model = rt.Identifier,
            choices, usage = UsageSum(results.Select(r => r.Stats).ToArray()), stats = StatsObj(stats),
        });
    }

    /// <summary>OpenAI chat logprobs object (content: token, logprob, bytes, top_logprobs) for a run of tokens.</summary>
    private static object? ChatLogprobs(IReadOnlyList<TokenLogprob> items)
    {
        if (items.Count == 0) return null;
        return new
        {
            content = items.Select(t => new
            {
                token = t.Token, logprob = t.Logprob, bytes = Encoding.UTF8.GetBytes(t.Token).Select(b => (int)b).ToList(),
                top_logprobs = t.Top.Select(x => new { token = x.Token, logprob = x.Logprob, bytes = Encoding.UTF8.GetBytes(x.Token).Select(b => (int)b).ToList() }).ToList(),
            }).ToList(),
        };
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
    };

    /// <summary>Legacy completions logprobs (tokens, token_logprobs, top_logprobs, text_offset).</summary>
    private static object? TextLogprobs(IReadOnlyList<TokenLogprob> items)
    {
        if (items.Count == 0) return null;
        int off = 0;
        var offsets = new List<int>();
        foreach (var t in items) { offsets.Add(off); off += t.Token.Length; }
        return new
        {
            tokens = items.Select(t => t.Token).ToList(),
            token_logprobs = items.Select(t => t.Logprob).ToList(),
            top_logprobs = items.Select(t => t.Top.GroupBy(x => x.Token).ToDictionary(x => x.Key, x => x.First().Logprob)).ToList(),
            text_offset = offsets,
        };
    }

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
                int sentLp = 0;
                await foreach (var seg in rt.StreamSegmentsAsync(prompt, ForChoice(i), st, ct))
                {
                    if (seg.Kind == SegmentKind.Reasoning && !g.KeepReasoning) continue;
                    object? lp = null;
                    if (g.Logprobs)
                    {
                        List<TokenLogprob> fresh;
                        lock (st.Logprobs) { fresh = st.Logprobs.Skip(sentLp).ToList(); sentLp += fresh.Count; }
                        lp = TextLogprobs(fresh);
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
                logprobs = g.Logprobs ? TextLogprobs(stats[i].Logprobs.ToList()) : null,
            }).ToList(),
            usage = UsageSum(stats), stats = StatsObj(stats[0]),
        });
    }

    private async Task Embeddings(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        var rt = await _host.Models.ResolveAsync(body["model"]?.GetValue<string>(), true, ct);
        var inputs = new List<string>();
        if (body["input"] is JsonArray arr) foreach (var x in arr) inputs.Add(x?.GetValue<string>() ?? "");
        else if (body["input"]?.GetValue<string>() is { } one) inputs.Add(one);
        if (inputs.Count == 0) { await WriteError(ctx, 400, "'input' is required", "invalid_request_error"); return; }

        var data = new List<object>();
        int tokens = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            var v = await rt.EmbedAsync(inputs[i], ct);
            tokens += rt.CountTokens(inputs[i]);
            data.Add(new { @object = "embedding", index = i, embedding = v });
        }
        await WriteJson(ctx, new
        {
            @object = "list", data, model = rt.Identifier,
            usage = new { prompt_tokens = tokens, total_tokens = tokens },
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
        j.Id, j.Repo, j.FileName, state = j.State.ToString().ToLowerInvariant(),
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
                    hw.Cpu, hw.LogicalCores, hw.TotalRamBytes,
                    npus = hw.Npus.Select(n => new { n.Name, n.Vendor, n.Architecture, n.Driver, n.Status }),
                    gpus = hw.Gpus.Select(g => new { g.Index, g.Name, g.Vendor, g.VramBytes, g.SharedBytes, g.UsableBytes, g.Integrated, g.Driver }),
                },
                loaded = _host.Models.Loaded.Select(r => new
                {
                    id = r.Identifier, file = r.Model.FileName, r.GpuLayers,
                    device = r.GpuLayers > 0 ? "gpu" : "cpu", vision = r.SupportsVision, capabilities = r.Capabilities.ToApi(), threads = r.Threads, contextSize = r.ContextSize, cachedTokens = r.CachedTokenCount,
                    sizeBytes = r.Model.SizeBytes, r.Requests, r.Busy, lastUsed = r.LastUsed,
                    reasoning = r.ReasoningStyle.ToString().ToLowerInvariant(), supportsReasoning = r.SupportsReasoning,
                    maxConcurrent = r.MaxConcurrent, active = r.ActiveRequests, queued = r.QueuedRequests, toolFormat = r.ToolFormat.ToString().ToLowerInvariant(),
                    warnings = r.LoadWarnings, estimate = r.Estimate == null ? null : new { r.Estimate.Verdict, summary = r.Estimate.Describe() },
                    gpuPlan = r.Plan == null ? null : new { devices = r.Plan.Ordinals, split = r.Plan.TensorSplit, mainGpu = r.Plan.MainGpu, splitMode = r.Plan.SplitMode },
                    isEmbedding = r.Model.IsEmbedding || r.Options.Embeddings,
                }),
                modelsDirectory = _host.Settings.ResolvedModelsDirectory,
            });
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

        app.MapGet("/api/models/local", async ctx =>
        {
            var loaded = _host.Models.Loaded;
            await WriteJson(ctx, _host.Library.List().Select(m => LocalModelDto(m, loaded)));
        });

        app.MapGet("/api/models/catalog", async ctx =>
            await WriteJson(ctx, Catalog.Entries));

        app.MapGet("/api/models/search", async ctx =>
            await WriteJson(ctx, await HuggingFace.SearchAsync(Q(ctx, "q"), 30, ctx.RequestAborted)));

        app.MapGet("/api/models/files", async ctx =>
            await WriteJson(ctx, (await HuggingFace.ListFilesAsync(Q(ctx, "repo"), ctx.RequestAborted))
                .Select(f => new { f.Repo, f.FileName, f.SizeBytes, f.Quant, f.IsSplit, f.Shards, vision = f.HasVision, mmproj = f.MmprojFile, mmprojBytes = f.MmprojSize })));

        // "will it fit?" estimate for a model + load settings (query values override the model's / global defaults)
        app.MapGet("/api/models/estimate", async ctx =>
        {
            var m = _host.Library.Find(Q(ctx, "id")) ?? throw new FileNotFoundException("Model not found");
            var o = (_host.ModelDefaults.LoadFor(m.Id) ?? _host.Settings.DefaultLoad).Clone();
            if (uint.TryParse(Q(ctx, "ctx"), out var c) && c > 0) o.ContextSize = c;
            if (Q(ctx, "device") is { Length: > 0 } dev) o.Device = dev;
            if (Q(ctx, "gpuLayers") is { Length: > 0 } gl) o.GpuLayers = int.TryParse(gl, out var g) ? g : null;
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
            var rt = await _host.Models.LoadAsync(model, opts, ctx.RequestAborted);
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
        app.MapPut("/api/presets/{name}", async ctx =>
        {
            var body = await ReadBody(ctx);
            var g = body.Deserialize<GenerationOptions>(Json.Options) ?? new GenerationOptions();
            _host.Presets.Save((string)ctx.Request.RouteValues["name"]!, g);
            await WriteJson(ctx, new { ok = true });
        });
        app.MapDelete("/api/presets/{name}", async ctx =>
            await WriteJson(ctx, new { deleted = _host.Presets.Delete((string)ctx.Request.RouteValues["name"]!) }));

        app.MapGet("/api/chats", async ctx =>
            await WriteJson(ctx, _host.Chats.List().Select(c => new { c.Id, c.Title, c.Model, c.UpdatedAt, count = c.Messages.Count })));
        app.MapGet("/api/chats/{id}", async ctx =>
        {
            var c = _host.Chats.Get((string)ctx.Request.RouteValues["id"]!);
            if (c == null) await WriteError(ctx, 404, "Chat not found", "not_found"); else await WriteJson(ctx, c);
        });
        app.MapPut("/api/chats/{id}", async ctx =>
        {
            var body = await ReadBody(ctx);
            var c = body.Deserialize<Conversation>(Json.Options) ?? new Conversation();
            c.Id = (string)ctx.Request.RouteValues["id"]!;
            await WriteJson(ctx, _host.Chats.Save(c));
        });
        app.MapDelete("/api/chats/{id}", async ctx =>
            await WriteJson(ctx, new { deleted = _host.Chats.Delete((string)ctx.Request.RouteValues["id"]!) }));

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
