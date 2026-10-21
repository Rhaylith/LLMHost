using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LLMHostProject.Server;

/// <summary>Anthropic Messages API compatibility layer (/v1/messages) over the local models.</summary>
public partial class ApiServer
{
    private void MapAnthropic(WebApplication app)
    {
        app.MapPost("/v1/messages", AnthropicMessages);
        app.MapPost("/v1/messages/count_tokens", AnthropicCountTokens);
    }

    private bool AnthropicEnabled => _host.Settings.AnthropicApi;

    /// <summary>
    /// Anthropic clients send names like "claude-sonnet-4"; map unknown names to the loaded model
    /// instead of failing, so existing SDK code works without changes.
    /// </summary>
    private async Task<ModelRuntime> ResolveAnthropicModel(string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            bool known = _host.Models.Loaded.Any(r => r.Identifier.Equals(requested, StringComparison.OrdinalIgnoreCase))
                         || _host.Library.Find(requested) != null;
            if (known) return await _host.Models.ResolveAsync(requested, false, ct);
        }
        return await _host.Models.ResolveAsync(null, false, ct);
    }

    private static string BlocksToText(JsonNode? content)
    {
        if (content == null) return "";
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is not JsonArray arr) return content.ToJsonString();
        var parts = new List<string>();
        foreach (var b in arr)
            if (b is JsonObject o && (o["type"]?.GetValue<string>() ?? "text") == "text" && o["text"]?.GetValue<string>() is { } t) parts.Add(t);
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Clients such as Claude Code send one user turn as several text blocks: injected context
    /// (<c>&lt;system-reminder&gt;</c>), the question, and bookkeeping tags. Small models latch onto whatever comes last,
    /// so bookkeeping-only blocks are dropped, context blocks go first and the user's own text goes last.
    /// </summary>
    internal static string OrderTextBlocks(IReadOnlyList<string> parts)
    {
        static bool Bookkeeping(string p) =>
            System.Text.RegularExpressions.Regex.IsMatch(p.Trim(), @"^<(total_tokens|token_usage|budget:token_budget)[^>]*>.*</\1>$",
                System.Text.RegularExpressions.RegexOptions.Singleline);
        static bool Context(string p) => p.TrimStart().StartsWith("<system-reminder>", StringComparison.Ordinal)
                                         && p.TrimEnd().EndsWith("</system-reminder>", StringComparison.Ordinal);
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p) && !Bookkeeping(p)).ToList();
        var ordered = kept.Where(Context).Concat(kept.Where(p => !Context(p)));
        return string.Join("\n\n", ordered);
    }

    internal static List<ChatMessage> ReadAnthropicMessages(JsonNode body, ToolFormat fmt = ToolFormat.Generic)
    {
        var list = new List<ChatMessage>();
        string system = BlocksToText(body["system"]);
        if (system.Length > 0) list.Add(new ChatMessage("system", system));

        if (body["messages"] is not JsonArray arr) return list;
        var names = new Dictionary<string, string>();      // tool_use id -> name
        foreach (var m in arr)
        {
            if (m is not JsonObject o) continue;
            // Claude Code puts "system" messages inside the conversation (environment details, a token counter, ...).
            // Anthropic's own API has no such role; here they are instructions, never something the user said:
            // they are appended to the system prompt, and bookkeeping-only ones are dropped.
            string rawRole = o["role"]?.GetValue<string>() ?? "user";
            if (rawRole is "system" or "developer")
            {
                string sysText = o["content"] is JsonArray sysBlocks
                    ? OrderTextBlocks(sysBlocks.OfType<JsonObject>().Where(b => (b["type"]?.GetValue<string>() ?? "text") == "text")
                        .Select(b => b["text"]?.GetValue<string>() ?? "").ToList())
                    : OrderTextBlocks(new[] { BlocksToText(o["content"]) });
                if (sysText.Length > 0)
                {
                    int si = list.FindIndex(x => x.Role == "system");
                    if (si >= 0) list[si] = list[si] with { Content = list[si].Content + "\n\n" + sysText };
                    else list.Insert(0, new ChatMessage("system", sysText));
                }
                continue;
            }
            string role = o["role"]?.GetValue<string>() == "assistant" ? "assistant" : "user";
            if (o["content"] is not JsonArray blocks)
            {
                list.Add(new ChatMessage(role, BlocksToText(o["content"])));
                continue;
            }
            var textParts = new List<string>();
            var images = new List<byte[]>();
            var toolUses = new List<(string, JsonNode?)>();
            var results = new List<string>();
            foreach (var b in blocks)
            {
                if (b is not JsonObject bo) continue;
                switch (bo["type"]?.GetValue<string>())
                {
                    case "text": if (bo["text"]?.GetValue<string>() is { } tp) textParts.Add(tp); break;
                    case "image":
                        {
                            var src = bo["source"];
                            string? kind = src?["type"]?.GetValue<string>();
                            if (kind == "base64" && src?["data"]?.GetValue<string>() is { } b64) images.Add(ImageInput.FromBase64(b64));
                            else if (kind == "url" && src?["url"]?.GetValue<string>() is { } u) images.Add(ImageInput.FromUrl(u));
                            break;
                        }
                    case "tool_use":
                        {
                            string nm = bo["name"]?.GetValue<string>() ?? "";
                            if (bo["id"]?.GetValue<string>() is { } uid) names[uid] = nm;
                            toolUses.Add((nm, bo["input"]?.DeepClone() ?? new JsonObject()));
                            break;
                        }
                    case "tool_result":
                        {
                            string uid = bo["tool_use_id"]?.GetValue<string>() ?? "";
                            string nm = names.TryGetValue(uid, out var n) ? n : uid;
                            results.Add(ToolFormats.Result(fmt, nm, BlocksToText(bo["content"])).Content);
                            break;
                        }
                }
            }
            var text = new StringBuilder(OrderTextBlocks(textParts));
            if (toolUses.Count > 0)
                list.Add(new ChatMessage("assistant", ToolFormats.EchoCalls(fmt, text.ToString(), toolUses)));
            else if (results.Count > 0)
                list.Add(new ChatMessage("user", string.Join("\n", results) + (text.Length > 0 ? "\n" + text : "")));
            else list.Add(new ChatMessage(role, text.ToString(), images.Count > 0 ? images : null));
        }
        return list;
    }

    private static GenerationOptions ReadAnthropicGeneration(JsonNode body, GenerationOptions defaults)
    {
        var g = OpenAiMapping.ReadGeneration(body, defaults);
        if (body["stop_sequences"] is JsonArray ss)
        {
            g.Stop = ss.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
        }
        if (body["system"] != null && g.SystemPrompt != null) g.SystemPrompt = null; // explicit system wins
        return g;
    }

    private static string StopReason(GenerationStats s) =>
        s.StopSequence != null ? "stop_sequence" : s.FinishReason == "length" ? "max_tokens" : "end_turn";

    private static async Task AnthropicEvent(HttpContext ctx, string name, object data)
    {
        await ctx.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json.Compact)}\n\n");
        await ctx.Response.Body.FlushAsync();
    }


    private async Task AnthropicMessages(HttpContext ctx)
    {
        if (!AnthropicEnabled) { await WriteError(ctx, 404, "The Anthropic API is disabled (config anthropic on)", "not_found"); return; }
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        if (body["messages"] is not JsonArray { Count: > 0 })
        { await WriteError(ctx, 400, "messages: field required", "invalid_request_error"); return; }

        if (_host.Settings.DebugRequests)
        {
            try
            {
                string dir = Path.Combine(AppPaths.Root, "debug");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "anthropic-last.json"), body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
        var rt = await ResolveAnthropicModel(body["model"]?.GetValue<string>(), ct);
        // shows in Logs: proves which client reached this server and which model name it asked for
        LogBuffer.Add("anthropic", $"POST /v1/messages from '{ctx.Request.Headers.UserAgent}' asked for model '{body["model"]?.GetValue<string>()}' -> serving '{rt.Identifier}' (stream={body["stream"]?.ToJsonString() ?? "false"}, tools={(body["tools"] as JsonArray)?.Count ?? 0})");
        var g = ReadAnthropicGeneration(body, _host.GenerationDefaultsFor(rt.Model.Id));
        var messages = ReadAnthropicMessages(body, rt.ToolFormat);
        var tools = OpenAiMapping.ReadAnthropicTools(body, rt.ToolFormat);

        if (!messages.Any(m => m.Role == "system") && !string.IsNullOrWhiteSpace(g.SystemPrompt))
            messages.Insert(0, new ChatMessage("system", g.SystemPrompt!));
        if (tools != null) OpenAiMapping.ApplyTools(tools, messages, g);

        int reserve = g.MaxTokens > 0 ? g.MaxTokens : (int)Math.Min(512, rt.ContextSize / 4);
        string prompt = rt.BuildFittingPrompt(messages, reserve, out _, out var images, g.ReasoningEffort);
        bool stream = body["stream"]?.GetValue<bool>() ?? false;
        string id = "msg_" + Guid.NewGuid().ToString("N")[..24];
        var stats = new GenerationStats();

        if (!stream)
        {
            var (text, thinking) = await rt.CompleteSegmentsAsync(prompt, g, stats, ct, images);
            var blocks = new List<object>();
            bool asText = _host.Settings.AnthropicReasoningAsText;
            string thinkPrefix = thinking.Length > 0 && asText ? "<think>\n" + thinking + "\n</think>\n\n" : "";
            if (thinking.Length > 0 && !asText) blocks.Add(new { type = "thinking", thinking, signature = "" });
            string stop = StopReason(stats);
            if (tools != null)
            {
                var (answer, calls) = ToolStreamParser.ParseAll(tools.Format, text);
                if (answer.Length > 0 || thinkPrefix.Length > 0 || calls.Count == 0) blocks.Add(new { type = "text", text = thinkPrefix + answer });
                foreach (var c in calls)
                {
                    JsonNode? input;
                    try { input = JsonNode.Parse(c.Arguments); } catch { input = new JsonObject(); }
                    blocks.Add(new { type = "tool_use", id = "toolu_" + Guid.NewGuid().ToString("N")[..20], name = c.Name, input = input ?? new JsonObject() });
                }
                if (calls.Count > 0) stop = "tool_use";
            }
            else blocks.Add(new { type = "text", text = thinkPrefix + text });
            await WriteJson(ctx, MessageObject(id, rt, blocks, stop, stats));
            return;
        }

        SseHeaders(ctx);
        int promptTokens = rt.CountTokens(prompt);
        await AnthropicEvent(ctx, "message_start", new
        {
            type = "message_start",
            message = new
            {
                id, type = "message", role = "assistant", model = rt.Identifier, content = Array.Empty<object>(),
                stop_reason = (string?)null, stop_sequence = (string?)null,
                usage = new { input_tokens = promptTokens, output_tokens = 1 },
            },
        });
        await AnthropicEvent(ctx, "ping", new { type = "ping" });

        // blocks are opened lazily: optional thinking, then text and/or tool_use blocks
        int blockIndex = -1;
        string? current = null;          // "thinking" | "text" | "tool:<callIndex>"
        async Task CloseBlock()
        {
            if (current != null) await AnthropicEvent(ctx, "content_block_stop", new { type = "content_block_stop", index = blockIndex });
            current = null;
        }
        async Task OpenBlock(string key, object contentBlock)
        {
            await CloseBlock();
            blockIndex++;
            current = key;
            await AnthropicEvent(ctx, "content_block_start", new { type = "content_block_start", index = blockIndex, content_block = contentBlock });
        }
        async Task Delta(object delta) =>
            await AnthropicEvent(ctx, "content_block_delta", new { type = "content_block_delta", index = blockIndex, delta });

        var parser = tools != null ? new ToolStreamParser(tools.Format) : null;
        async Task Handle(ToolEvent ev)
        {
            switch (ev)
            {
                case ContentEvent c:
                    if (current != "text") await OpenBlock("text", new { type = "text", text = "" });
                    await Delta(new { type = "text_delta", text = c.Text });
                    break;
                case CallStartEvent s:
                    await OpenBlock("tool:" + s.Index, new { type = "tool_use", id = "toolu_" + s.Id["call_".Length..], name = s.Name, input = new { } });
                    break;
                case ArgsDeltaEvent a:
                    await Delta(new { type = "input_json_delta", partial_json = a.Text });
                    break;
                case CallEndEvent:
                    await CloseBlock();
                    break;
            }
        }

        // Prompt processing of a large prompt (Claude Code sends ~20k tokens) produces no output for a while:
        // keep the connection alive with periodic pings until the first token arrives.
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), pingCts.Token);
                    await AnthropicEvent(ctx, "ping", new { type = "ping" });
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        });
        async Task StopPinger()
        {
            if (pingCts.IsCancellationRequested) return;
            pingCts.Cancel();
            try { await pinger; } catch { }
        }

        // Most clients (Claude Code CLI) do not render "thinking" blocks while they stream, so a long reasoning phase
        // looks like a stall. Sending the reasoning as ordinary text shows it as it is produced.
        bool reasoningAsText = _host.Settings.AnthropicReasoningAsText;
        bool thinkOpen = false;
        async Task CloseThinkText()
        {
            if (!thinkOpen) return;
            thinkOpen = false;
            if (current != "text") await OpenBlock("text", new { type = "text", text = "" });
            await Delta(new { type = "text_delta", text = "\n</think>\n\n" });
        }

        try
        {
            await foreach (var seg in rt.StreamSegmentsAsync(prompt, g, stats, ct, images))
            {
                await StopPinger();
                if (seg.Kind == SegmentKind.Reasoning)
                {
                    if (reasoningAsText)
                    {
                        if (current != "text") await OpenBlock("text", new { type = "text", text = "" });
                        if (!thinkOpen) { thinkOpen = true; await Delta(new { type = "text_delta", text = "<think>\n" }); }
                        await Delta(new { type = "text_delta", text = seg.Text });
                    }
                    else
                    {
                        if (current != "thinking") await OpenBlock("thinking", new { type = "thinking", thinking = "" });
                        await Delta(new { type = "thinking_delta", thinking = seg.Text });
                    }
                    continue;
                }
                await CloseThinkText();
                if (parser != null) foreach (var ev in parser.Feed(seg.Text)) await Handle(ev);
                else await Handle(new ContentEvent(seg.Text));
            }
        }
        finally { await StopPinger(); }
        await CloseThinkText();
        if (parser != null) foreach (var ev in parser.Finish()) await Handle(ev);
        if (blockIndex < 0) await OpenBlock("text", new { type = "text", text = "" });
        await CloseBlock();

        string stopReason = parser is { HasCalls: true } ? "tool_use" : StopReason(stats);
        await AnthropicEvent(ctx, "message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = stats.StopSequence },
            usage = new { output_tokens = stats.CompletionTokens },
        });
        await AnthropicEvent(ctx, "message_stop", new { type = "message_stop" });
    }

    private static object MessageObject(string id, ModelRuntime rt, List<object> content, string? stop, GenerationStats s, bool startOnly = false) => new
    {
        id, type = "message", role = "assistant", model = rt.Identifier, content,
        stop_reason = stop,
        stop_sequence = startOnly ? null : s.StopSequence,
        usage = new { input_tokens = Math.Max(0, s.PromptTokens - s.CachedTokens), cache_read_input_tokens = s.CachedTokens, output_tokens = startOnly ? 1 : s.CompletionTokens },
    };


    private async Task AnthropicCountTokens(HttpContext ctx)
    {
        if (!AnthropicEnabled) { await WriteError(ctx, 404, "The Anthropic API is disabled", "not_found"); return; }
        var body = await ReadBody(ctx);
        var rt = await ResolveAnthropicModel(body["model"]?.GetValue<string>(), ctx.RequestAborted);
        var messages = ReadAnthropicMessages(body, rt.ToolFormat);
        var tools = OpenAiMapping.ReadAnthropicTools(body, rt.ToolFormat);
        if (tools != null) OpenAiMapping.ApplyTools(tools, messages, new GenerationOptions());
        await WriteJson(ctx, new { input_tokens = rt.CountTokens(rt.ApplyChatTemplate(messages)) });
    }
}
