using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LLMHostProject.Server;

/// <summary>
/// Native REST API v1 (/api/v1/*): stateful chat with typed output items and streaming events, model listing,
/// load / unload and download management.
/// </summary>
public partial class ApiServer
{
    /// <summary>Messages of earlier native chats, for previous_response_id continuation.</summary>
    private readonly ConcurrentDictionary<string, (List<ChatMessage> Messages, DateTime At)> _nativeChats = new();

    private void MapNative(WebApplication app)
    {
        app.MapGet("/api/v1/models", async ctx =>
        {
            var loaded = _host.Models.Loaded;
            var models = _host.Library.List().Select(m => NativeModelDto(m, loaded)).ToList();
            await WriteJson(ctx, new { models });
        });

        app.MapPost("/api/v1/models/load", async ctx =>
        {
            var body = await ReadBody(ctx);
            string model = body["model"]?.GetValue<string>() ?? throw new ArgumentException("'model' is required");
            var local = _host.Library.Find(model) ?? throw new FileNotFoundException($"No downloaded model matches '{model}'.");
            var opts = (_host.ModelDefaults.LoadFor(local.Id) ?? _host.Settings.DefaultLoad).Clone();
            if (body["context_length"] is JsonValue cl && cl.TryGetValue<uint>(out var cv)) opts.ContextSize = cv;
            if (body["flash_attention"] is JsonValue fa && fa.TryGetValue<bool>(out var fv)) opts.FlashAttentionMode = fv ? "on" : "off";
            if (body["eval_batch_size"] is JsonValue eb && eb.TryGetValue<uint>(out var ev)) opts.BatchSize = opts.UBatchSize = ev;
            if (body["num_experts"] != null) { /* accepted for compatibility; MoE expert count is not configurable yet */ }
            if (body["gpu"] is JsonValue gv && gv.TryGetValue<string>(out var gs))
                opts.Device = gs is "off" or "cpu" ? "cpu" : gs is "max" or "all" ? "gpu" : "auto";
            if (body["identifier"]?.GetValue<string>() is { Length: > 0 } ident) opts.Identifier = ident;
            if (body["parallel"] is JsonValue pv && pv.TryGetValue<int>(out var pn)) opts.MaxConcurrent = pn;
            if (body["ttl"] is JsonValue tv && tv.TryGetValue<int>(out var tn)) opts.TtlMinutes = Math.Max(0, tn / 60);
            var sw = Stopwatch.StartNew();
            var rt = await _host.Models.LoadAsync(model, opts, ctx.RequestAborted);
            var result = new JsonObject
            {
                ["type"] = rt.Model.IsEmbedding || rt.Options.Embeddings ? "embedding" : "llm",
                ["instance_id"] = rt.Identifier, ["load_time_seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 3), ["status"] = "loaded",
            };
            if (body["echo_load_config"]?.GetValue<bool>() == true)
                result["load_config"] = new JsonObject
                {
                    ["context_length"] = rt.ContextSize, ["flash_attention"] = rt.Options.FlashAttentionSetting,
                    ["eval_batch_size"] = rt.Options.BatchSize, ["parallel"] = rt.MaxConcurrent,
                    ["gpu_layers"] = rt.GpuLayers,
                };
            await WriteJson(ctx, result);
        });

        app.MapPost("/api/v1/models/unload", async ctx =>
        {
            var body = await ReadBody(ctx);
            string id = body["instance_id"]?.GetValue<string>() ?? body["model"]?.GetValue<string>() ?? throw new ArgumentException("'instance_id' is required");
            if (!_host.Models.Unload(id)) { await WriteError(ctx, 404, $"No loaded model instance '{id}'", "not_found"); return; }
            await WriteJson(ctx, new { instance_id = id });
        });

        app.MapPost("/api/v1/models/download", async ctx =>
        {
            var body = await ReadBody(ctx);
            string input = body["model"]?.GetValue<string>() ?? body["url"]?.GetValue<string>() ?? throw new ArgumentException("'model' is required");
            var job = await StartDownloadFromInput(input, body["quantization"]?.GetValue<string>(), true, ctx.RequestAborted);
            await WriteJson(ctx, NativeJobDto(job));
        });

        app.MapGet("/api/v1/models/download/status", async ctx =>
        {
            string id = Q(ctx, "job_id");
            if (id.Length > 0)
            {
                var j = _host.Downloads.Get(id);
                if (j == null) { await WriteError(ctx, 404, $"No download job '{id}'", "not_found"); return; }
                await WriteJson(ctx, NativeJobDto(j));
                return;
            }
            await WriteJson(ctx, new { jobs = _host.Downloads.Jobs.Select(NativeJobDto) });
        });
        app.MapGet("/api/v1/models/download/status/{id}", async ctx =>
        {
            var j = _host.Downloads.Get((string)ctx.Request.RouteValues["id"]!);
            if (j == null) { await WriteError(ctx, 404, "No such download job", "not_found"); return; }
            await WriteJson(ctx, NativeJobDto(j));
        });

        app.MapPost("/api/v1/chat", NativeChat);
    }

    private static object NativeJobDto(DownloadJob j) => new
    {
        job_id = j.Id,
        status = j.State switch
        {
            DownloadState.Completed => "completed", DownloadState.Failed => "failed",
            DownloadState.Cancelled => "cancelled", DownloadState.Paused => "paused", _ => "downloading",
        },
        model = j.Repo + "/" + j.FileName,
        total_size_bytes = j.TotalBytes, downloaded_bytes = Interlocked.Read(ref j.BytesDone),
        bytes_per_second = j.BytesPerSecond, progress = j.Progress, error = j.Error, path = j.FinalPath, verified = j.Verified,
    };

    private static object NativeModelDto(LocalModel m, IReadOnlyList<ModelRuntime> loaded)
    {
        string[] parts = m.Id.Split('/');
        var inst = loaded.Where(l => l.Model.Path == m.Path).Select(l => new
        {
            id = l.Identifier,
            config = new { context_length = l.ContextSize, flash_attention = l.Options.FlashAttentionSetting, parallel = l.MaxConcurrent, gpu_layers = l.GpuLayers },
        }).ToList();
        return new
        {
            type = m.IsEmbedding ? "embedding" : "llm",
            publisher = parts.Length > 2 ? parts[0] : "local",
            key = m.Id,
            display_name = m.Name,
            architecture = m.Architecture,
            quantization = new { name = m.Quantization },
            size_bytes = m.SizeBytes,
            params_string = m.SizeLabel,
            format = "gguf",
            max_context_length = m.ContextLength,
            loaded_instances = inst,
            capabilities = new { vision = m.SupportsVision, trained_for_tool_use = m.HasChatTemplate },
        };
    }

    // ---------------------------------------------------------------- POST /api/v1/chat

    private static async Task NativeEvent(HttpContext ctx, string name, object data)
    {
        await ctx.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json.Compact)}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    private async Task NativeChat(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        string? modelName = body["model"]?.GetValue<string>();
        bool stream = body["stream"]?.GetValue<bool>() ?? false;

        // ---- model (loaded on demand; reported as events when streaming)
        double loadSeconds = 0;
        ModelRuntime rt;
        var already = modelName != null ? _host.Models.Loaded.FirstOrDefault(r => r.Identifier.Equals(modelName, StringComparison.OrdinalIgnoreCase)) : _host.Models.Loaded.FirstOrDefault();
        bool jit = already == null;
        if (stream) SseHeaders(ctx);
        if (jit && stream) await NativeEvent(ctx, "model_load.start", new { type = "model_load.start", model_instance_id = modelName });
        var swLoad = Stopwatch.StartNew();
        rt = already ?? await _host.Models.ResolveAsync(modelName, false, ct);
        if (jit) loadSeconds = swLoad.Elapsed.TotalSeconds;
        if (jit && stream) await NativeEvent(ctx, "model_load.end", new { type = "model_load.end", model_instance_id = rt.Identifier, load_time_seconds = Math.Round(loadSeconds, 3) });

        // ---- request -> messages / generation options
        var g = _host.GenerationDefaultsFor(rt.Model.Id).Clone();
        if (OpenAiMapping.Num(body, "temperature") is { } t) g.Temperature = (float)t;
        if (OpenAiMapping.Num(body, "top_p") is { } tp) g.TopP = (float)tp;
        if (OpenAiMapping.Num(body, "top_k") is { } tk) g.TopK = (int)tk;
        if (OpenAiMapping.Num(body, "min_p") is { } mp) g.MinP = (float)mp;
        if (OpenAiMapping.Num(body, "repeat_penalty") is { } rp) g.RepeatPenalty = (float)rp;
        if (OpenAiMapping.Num(body, "max_output_tokens") is { } mo) g.MaxTokens = (int)mo;
        if (OpenAiMapping.Num(body, "seed") is { } sd && sd >= 0) g.Seed = (uint)sd;
        OpenAiMapping.ReadSamplers(body, g);
        if (body["reasoning"] is JsonValue rv && rv.TryGetValue<string>(out var rs) && Reasoning.NormalizeEffort(rs) is { } eff) g.ReasoningEffort = eff;

        var messages = new List<ChatMessage>();
        if (body["previous_response_id"]?.GetValue<string>() is { Length: > 0 } prev)
        {
            if (!_nativeChats.TryGetValue(prev, out var p)) { await WriteError(ctx, 404, $"previous_response_id '{prev}' was not found", "not_found"); return; }
            messages.AddRange(p.Messages);
        }
        string? system = body["system_prompt"]?.GetValue<string>() ?? g.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(system) && !messages.Any(m => m.Role == "system")) messages.Insert(0, new ChatMessage("system", system));

        var images = new List<byte[]>();
        var text = new StringBuilder();
        if (body["input"] is JsonValue iv && iv.TryGetValue<string>(out var ins)) text.Append(ins);
        else if (body["input"] is JsonArray ia)
            foreach (var part in ia)
            {
                if (part is JsonValue pv && pv.TryGetValue<string>(out var ps)) { text.Append(ps); continue; }
                if (part is not JsonObject po) continue;
                switch (po["type"]?.GetValue<string>())
                {
                    case "text": text.Append(po["content"]?.GetValue<string>() ?? po["text"]?.GetValue<string>()); break;
                    case "image":
                        if ((po["data_url"] ?? po["url"])?.GetValue<string>() is { } url) images.Add(ImageInput.FromUrl(url, po["detail"]?.GetValue<string>()));
                        break;
                }
            }
        else { await WriteError(ctx, 400, "'input' is required (a string or an array of text / image parts)", "invalid_request_error"); return; }
        messages.Add(new ChatMessage("user", text.ToString(), images.Count > 0 ? images : null));

        var history = messages.ToList();
        int reserve = g.MaxTokens > 0 ? g.MaxTokens : (int)Math.Min(512, rt.ContextSize / 4);
        string prompt = rt.BuildFittingPrompt(messages, reserve, out _, out var imgs, g.ReasoningEffort, g.ContextOverflow);
        var stats = new GenerationStats();
        string responseId = "resp_" + Guid.NewGuid().ToString("N")[..24];

        void Remember(string answer)
        {
            history.Add(new ChatMessage("assistant", answer));
            _nativeChats[responseId] = (history, DateTime.UtcNow);
            if (_nativeChats.Count > 200)
                foreach (var old in _nativeChats.OrderBy(x => x.Value.At).Take(_nativeChats.Count - 200)) _nativeChats.TryRemove(old.Key, out _);
        }
        object StatsDto(int reasoningTokens) => new
        {
            input_tokens = stats.PromptTokens, total_output_tokens = stats.CompletionTokens, reasoning_output_tokens = reasoningTokens,
            tokens_per_second = Math.Round(stats.TokensPerSecond, 2), time_to_first_token_seconds = Math.Round(stats.TimeToFirstTokenMs / 1000.0, 3),
            model_load_time_seconds = jit ? Math.Round(loadSeconds, 3) : (double?)null,
            context_shifts = stats.ContextShifts > 0 ? stats.ContextShifts : (int?)null,
            draft_tokens_proposed = stats.DraftTokens > 0 ? stats.DraftTokens : (int?)null,
            draft_tokens_accepted = stats.DraftTokens > 0 ? stats.DraftAccepted : (int?)null,
        };

        // ---- integrations: MCP tools the model may call; the server runs them and continues until it answers
        if (body["integrations"] is JsonArray { Count: > 0 } integrations)
        {
            var (toolsArr, map, temp) = await ResolveIntegrations(integrations, ct);
            try
            {
                var setup = OpenAiMapping.ReadTools(new JsonObject { ["tools"] = toolsArr }, rt.ToolFormat);
                var fmt = setup?.Format ?? rt.ToolFormat;
                var items = new List<object>();
                var finalAnswer = new StringBuilder();
                int reasoningLen2 = 0, answerLen2 = 0;
                if (stream)
                {
                    await NativeEvent(ctx, "chat.start", new { type = "chat.start", model_instance_id = rt.Identifier });
                    await NativeEvent(ctx, "prompt_processing.start", new { type = "prompt_processing.start" });
                    await NativeEvent(ctx, "prompt_processing.end", new { type = "prompt_processing.end" });
                }
                string? openKind = null;
                async Task CloseKind()
                {
                    if (openKind != null && stream) await NativeEvent(ctx, openKind + ".end", new { type = openKind + ".end" });
                    openKind = null;
                }
                async Task Emit(string kind, string content)
                {
                    if (!stream) return;
                    if (openKind != kind)
                    {
                        await CloseKind();
                        openKind = kind;
                        await NativeEvent(ctx, kind + ".start", new { type = kind + ".start" });
                    }
                    await NativeEvent(ctx, kind + ".delta", new { type = kind + ".delta", content });
                }

                for (int round = 0; round < 8; round++)
                {
                    var work = messages.ToList();
                    if (setup != null) OpenAiMapping.ApplyTools(setup, work, new GenerationOptions());
                    string roundPrompt = rt.BuildFittingPrompt(work, reserve, out _, out var im, g.ReasoningEffort, g.ContextOverflow);
                    var st = new GenerationStats();
                    var parser = setup != null ? new ToolStreamParser(setup.Format) : null;
                    var answer = new StringBuilder();
                    var thinking2 = new StringBuilder();

                    async Task Content(string text)
                    {
                        answer.Append(text); answerLen2 += text.Length;
                        await Emit("message", text);
                    }
                    await foreach (var seg in rt.StreamSegmentsAsync(roundPrompt, g, st, ct, im))
                    {
                        if (seg.Kind == SegmentKind.Reasoning)
                        {
                            thinking2.Append(seg.Text); reasoningLen2 += seg.Text.Length;
                            await Emit("reasoning", seg.Text);
                        }
                        else if (parser != null)
                        { foreach (var ev in parser.Feed(seg.Text)) if (ev is ContentEvent c) await Content(c.Text); }
                        else await Content(seg.Text);
                    }
                    if (parser != null) foreach (var ev in parser.Finish()) if (ev is ContentEvent c) await Content(c.Text);
                    await CloseKind();

                    stats.PromptTokens = st.PromptTokens; stats.CachedTokens = st.CachedTokens;
                    stats.CompletionTokens += st.CompletionTokens; stats.TotalMs += st.TotalMs;
                    if (round == 0) stats.TimeToFirstTokenMs = st.TimeToFirstTokenMs;

                    if (thinking2.Length > 0) items.Add(new { type = "reasoning", content = thinking2.ToString() });
                    if (answer.Length > 0) { items.Add(new { type = "message", content = answer.ToString() }); finalAnswer.Append(answer); }
                    var calls = parser?.Calls ?? new List<ParsedToolCall>();
                    if (calls.Count == 0) break;

                    // echo the model's own calls, run them, feed the results back
                    messages.Add(new ChatMessage("assistant", ToolFormats.EchoCalls(fmt, answer.ToString(), calls.Select(c =>
                    {
                        JsonNode? a; try { a = JsonNode.Parse(c.Arguments); } catch { a = new JsonObject(); }
                        return (c.Name, a);
                    }))));
                    foreach (var c in calls)
                    {
                        JsonNode? argNode; try { argNode = JsonNode.Parse(c.Arguments); } catch { argNode = new JsonObject(); }
                        object provider = map.TryGetValue(c.Name, out var it)
                            ? new { type = it.ProviderType, plugin_id = (string?)it.ProviderId, server_label = (string?)it.ServerLabel }
                            : new { type = "unknown", plugin_id = (string?)null, server_label = (string?)null };
                        string toolShown = map.TryGetValue(c.Name, out var it2) ? it2.Tool : c.Name;
                        if (stream) await NativeEvent(ctx, "tool_call.start", new { type = "tool_call.start", tool = toolShown, provider_info = provider });
                        if (stream) await NativeEvent(ctx, "tool_call.arguments", new { type = "tool_call.arguments", tool = toolShown, arguments = argNode, provider_info = provider });
                        string output; bool failed = false; IReadOnlyList<byte[]>? toolImages = null;
                        if (!map.TryGetValue(c.Name, out var found)) { output = $"Tool '{c.Name}' is not available."; failed = true; }
                        else
                        {
                            try
                            {
                                var res = await found.Client.CallAsync(found.Tool, argNode, ct);
                                output = res.Text; failed = res.IsError; toolImages = res.Images;
                            }
                            catch (Exception ex) { output = "Tool call failed: " + ex.Message; failed = true; }
                        }
                        items.Add(new { type = "tool_call", tool = toolShown, arguments = argNode, output, provider_info = provider });
                        if (stream)
                            await NativeEvent(ctx, failed ? "tool_call.failure" : "tool_call.success",
                                new { type = failed ? "tool_call.failure" : "tool_call.success", tool = toolShown, arguments = argNode, output, provider_info = provider });
                        var (rr, rc) = ToolFormats.Result(fmt, c.Name, output);
                        messages.Add(new ChatMessage(rr, rc));
                        if (found != null && OpenAiMapping.ToolImagesMessage(toolShown, toolImages) is { } shownImages) messages.Add(shownImages);
                    }
                }

                int rTok2 = reasoningLen2 + answerLen2 > 0 ? (int)Math.Round(stats.CompletionTokens * (double)reasoningLen2 / (reasoningLen2 + answerLen2)) : 0;
                Remember(finalAnswer.ToString());
                if (stream)
                    await NativeEvent(ctx, "chat.end", new { type = "chat.end", result = new { model_instance_id = rt.Identifier, output = items, stats = StatsDto(rTok2), response_id = responseId } });
                else
                    await WriteJson(ctx, new { model_instance_id = rt.Identifier, output = items, stats = StatsDto(rTok2), response_id = responseId });
            }
            finally { foreach (var c in temp) c.Dispose(); }
            return;
        }

        if (!stream)
        {
            var (answer, thinking) = await rt.CompleteSegmentsAsync(prompt, g, stats, ct, imgs);
            var output = new List<object>();
            if (thinking.Length > 0) output.Add(new { type = "reasoning", content = thinking });
            output.Add(new { type = "message", content = answer });
            int rTok = thinking.Length + answer.Length > 0 ? (int)Math.Round(stats.CompletionTokens * (double)thinking.Length / (thinking.Length + answer.Length)) : 0;
            Remember(answer);
            await WriteJson(ctx, new { model_instance_id = rt.Identifier, output, stats = StatsDto(rTok), response_id = responseId });
            return;
        }

        // ---- streaming events
        await NativeEvent(ctx, "chat.start", new { type = "chat.start", model_instance_id = rt.Identifier });
        await NativeEvent(ctx, "prompt_processing.start", new { type = "prompt_processing.start" });
        string? open = null;                 // "reasoning" | "message"
        var all = new StringBuilder();
        int reasoningLen = 0, answerLen = 0;
        bool promptDone = false;
        async Task End()
        {
            if (open != null) await NativeEvent(ctx, open + ".end", new { type = open + ".end" });
            open = null;
        }
        await foreach (var seg in rt.StreamSegmentsAsync(prompt, g, stats, ct, imgs))
        {
            if (!promptDone)
            {
                promptDone = true;
                await NativeEvent(ctx, "prompt_processing.end", new { type = "prompt_processing.end" });
            }
            string kind = seg.Kind == SegmentKind.Reasoning ? "reasoning" : "message";
            if (open != kind)
            {
                await End();
                open = kind;
                await NativeEvent(ctx, kind + ".start", new { type = kind + ".start" });
            }
            if (kind == "reasoning") reasoningLen += seg.Text.Length; else { answerLen += seg.Text.Length; all.Append(seg.Text); }
            await NativeEvent(ctx, kind + ".delta", new { type = kind + ".delta", content = seg.Text });
        }
        if (!promptDone) await NativeEvent(ctx, "prompt_processing.end", new { type = "prompt_processing.end" });
        await End();
        Remember(all.ToString());
        int rt2 = reasoningLen + answerLen > 0 ? (int)Math.Round(stats.CompletionTokens * (double)reasoningLen / (reasoningLen + answerLen)) : 0;
        await NativeEvent(ctx, "chat.end", new
        {
            type = "chat.end",
            result = new { model_instance_id = rt.Identifier, stats = StatsDto(rt2), response_id = responseId },
        });
    }
}
