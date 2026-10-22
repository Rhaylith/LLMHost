using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LLMHostProject.Server;

/// <summary>OpenAI Responses API (/v1/responses): items in, items out, streaming events, previous_response_id.</summary>
public partial class ApiServer
{
    /// <summary>Conversation items (input + output) of finished responses, so previous_response_id can continue them.</summary>
    private readonly ConcurrentDictionary<string, (List<JsonNode> Items, JsonObject Response, DateTime At)> _responses = new();
    private const int MaxStoredResponses = 500;

    private void MapResponses(WebApplication app)
    {
        app.MapPost("/v1/responses", CreateResponse);
        app.MapGet("/v1/responses/{id}", async ctx =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            if (_responses.TryGetValue(id, out var r)) await WriteJson(ctx, r.Response);
            else await WriteError(ctx, 404, $"No response with id '{id}'", "not_found");
        });
        app.MapDelete("/v1/responses/{id}", async ctx =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            await WriteJson(ctx, new { id, @object = "response", deleted = _responses.TryRemove(id, out _) });
        });
    }

    // ---------------------------------------------------------------- input items -> chat messages

    private static string PartsText(JsonNode? content, List<byte[]> images)
    {
        if (content == null) return "";
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        var sb = new StringBuilder();
        if (content is JsonArray parts)
            foreach (var p in parts)
            {
                if (p is not JsonObject o) continue;
                switch (o["type"]?.GetValue<string>())
                {
                    case "input_text": case "output_text": case "text":
                        sb.Append(o["text"]?.GetValue<string>());
                        break;
                    case "input_audio":
                        if (o["input_audio"]?["data"]?.GetValue<string>() is { Length: > 0 } ad)
                            images.Add(ImageInput.FromAudio(ad, o["input_audio"]?["format"]?.GetValue<string>()));
                        break;
                    case "input_image": case "image_url":
                        {
                            var iu = o["image_url"];
                            string? url = iu is JsonObject io ? io["url"]?.GetValue<string>() : iu?.GetValue<string>();
                            if (!string.IsNullOrEmpty(url)) images.Add(ImageInput.FromUrl(url, (iu as JsonObject)?["detail"]?.GetValue<string>() ?? o["detail"]?.GetValue<string>()));
                            break;
                        }
                }
            }
        return sb.ToString();
    }

    internal static List<ChatMessage> ResponsesMessages(IEnumerable<JsonNode> items, string? instructions, ToolFormat fmt)
    {
        var list = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(instructions)) list.Add(new ChatMessage("system", instructions));
        var names = new Dictionary<string, string>();
        var pendingCalls = new List<(string, JsonNode?)>();
        void FlushCalls()
        {
            if (pendingCalls.Count == 0) return;
            list.Add(new ChatMessage("assistant", ToolFormats.EchoCalls(fmt, null, pendingCalls)));
            pendingCalls = new List<(string, JsonNode?)>();
        }
        foreach (var item in items)
        {
            if (item is JsonValue sv && sv.TryGetValue<string>(out var str)) { FlushCalls(); list.Add(new ChatMessage("user", str)); continue; }
            if (item is not JsonObject o) continue;
            string type = o["type"]?.GetValue<string>() ?? (o["role"] != null ? "message" : "");
            switch (type)
            {
                case "message":
                    {
                        FlushCalls();
                        string role = o["role"]?.GetValue<string>() ?? "user";
                        if (role == "developer") role = "system";
                        var images = new List<byte[]>();
                        string text = PartsText(o["content"], images);
                        list.Add(new ChatMessage(role, text, images.Count > 0 ? images : null));
                        break;
                    }
                case "function_call":
                    {
                        string name = o["name"]?.GetValue<string>() ?? "";
                        string callId = o["call_id"]?.GetValue<string>() ?? o["id"]?.GetValue<string>() ?? "";
                        names[callId] = name;
                        JsonNode? args = null;
                        if (o["arguments"] is JsonValue av && av.TryGetValue<string>(out var astr))
                        { try { args = JsonNode.Parse(astr); } catch { args = JsonValue.Create(astr); } }
                        else args = o["arguments"]?.DeepClone();
                        pendingCalls.Add((name, args));
                        break;
                    }
                case "function_call_output":
                    {
                        FlushCalls();
                        string callId = o["call_id"]?.GetValue<string>() ?? "";
                        var outImages = new List<byte[]>();
                        string output = o["output"] is JsonValue ov && ov.TryGetValue<string>(out var os) ? os
                            : o["output"] is JsonArray oa ? OpenAiMapping.ContentText(oa, outImages) : o["output"]?.ToJsonString() ?? "";
                        string toolName = names.TryGetValue(callId, out var n) ? n : callId;
                        var (r, c) = ToolFormats.Result(fmt, toolName, output);
                        list.Add(new ChatMessage(r, c));
                        if (OpenAiMapping.ToolImagesMessage(toolName, outImages) is { } shown) list.Add(shown);
                        break;
                    }
                // reasoning items from earlier turns are not sent back to the model
            }
        }
        FlushCalls();
        return list;
    }

    // ---------------------------------------------------------------- the endpoint

    private static async Task RespEvent(HttpContext ctx, int seq, string type, JsonObject data)
    {
        data["type"] = type;
        data["sequence_number"] = seq;
        await ctx.Response.WriteAsync($"event: {type}\ndata: {data.ToJsonString()}\n\n");
        await ctx.Response.Body.FlushAsync();
    }

    private async Task CreateResponse(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var body = await ReadBody(ctx);
        var rt = await _host.Models.ResolveAsync(body["model"]?.GetValue<string>(), false, ct);
        var g = OpenAiMapping.ReadGeneration(body, _host.GenerationDefaultsFor(rt.Model.Id));
        var tools = OpenAiMapping.ReadTools(body, rt.ToolFormat);
        bool stream = body["stream"]?.GetValue<bool>() ?? false;
        bool store = body["store"]?.GetValue<bool>() ?? true;

        // conversation so far: stored items of previous_response_id + this request's input
        var history = new List<JsonNode>();
        if (body["previous_response_id"]?.GetValue<string>() is { Length: > 0 } prev)
        {
            if (!_responses.TryGetValue(prev, out var p)) { await WriteError(ctx, 404, $"previous_response_id '{prev}' was not found", "not_found"); return; }
            history.AddRange(p.Items.Select(x => x.DeepClone()));
        }
        var inputItems = new List<JsonNode>();
        if (body["input"] is JsonArray ia) inputItems.AddRange(ia.Where(x => x != null).Select(x => x!.DeepClone()));
        else if (body["input"] is JsonValue iv && iv.TryGetValue<string>(out var istr))
            inputItems.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = istr });
        if (inputItems.Count == 0 && history.Count == 0) { await WriteError(ctx, 400, "'input' is required", "invalid_request_error"); return; }

        string? instructions = body["instructions"]?.GetValue<string>() ?? g.SystemPrompt;
        var messages = ResponsesMessages(history.Concat(inputItems), instructions, rt.ToolFormat);
        if (tools != null) OpenAiMapping.ApplyTools(tools, messages, g);
        int reserve = g.MaxTokens > 0 ? g.MaxTokens : (int)Math.Min(512, rt.ContextSize / 4);
        var prompt = rt.BuildFittingPrompt(messages, reserve, out _, out var images, g.ReasoningEffort, g.ContextOverflow);

        string respId = "resp_" + Guid.NewGuid().ToString("N")[..24];
        long created = UnixNow();
        var stats = new GenerationStats();
        var output = new List<JsonNode>();
        int reasoningChars = 0, contentChars = 0;

        JsonObject Skeleton(string status) => new()
        {
            ["id"] = respId, ["object"] = "response", ["created_at"] = created, ["status"] = status, ["model"] = rt.Identifier,
            ["output"] = new JsonArray(output.Select(x => x.DeepClone()).ToArray()),
            ["instructions"] = instructions, ["previous_response_id"] = body["previous_response_id"]?.DeepClone(),
            ["parallel_tool_calls"] = tools?.Parallel ?? true, ["store"] = store,
            ["temperature"] = g.Temperature, ["top_p"] = g.TopP, ["max_output_tokens"] = g.MaxTokens > 0 ? g.MaxTokens : null,
            ["tools"] = body["tools"]?.DeepClone() ?? new JsonArray(), ["tool_choice"] = body["tool_choice"]?.DeepClone() ?? "auto",
            ["metadata"] = body["metadata"]?.DeepClone() ?? new JsonObject(),
            ["usage"] = status == "completed" || status == "incomplete" ? UsageObj(stats, reasoningChars, contentChars) : null,
            ["incomplete_details"] = status == "incomplete" ? new JsonObject { ["reason"] = "max_output_tokens" } : null,
        };

        JsonObject OutputText(string text, JsonArray? lp)
        {
            var part = new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() };
            if (g.Logprobs) part["logprobs"] = lp?.DeepClone() ?? new JsonArray();
            return part;
        }
        JsonObject MessageItem(string itemId, string text, JsonArray? lp = null) => new()
        {
            ["id"] = itemId, ["type"] = "message", ["status"] = "completed", ["role"] = "assistant",
            ["content"] = new JsonArray(OutputText(text, lp)),
        };
        JsonObject ReasoningItem(string itemId, string text) => new()
        {
            ["id"] = itemId, ["type"] = "reasoning",
            ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = text }),
        };
        JsonObject CallItem(string itemId, string callId, string name, string args) => new()
        {
            ["id"] = itemId, ["type"] = "function_call", ["status"] = "completed", ["call_id"] = callId, ["name"] = name, ["arguments"] = args,
        };

        void Remember(JsonObject response)
        {
            if (!store) return;
            var items = history.Concat(inputItems).Concat(output.Select(x => x.DeepClone())).ToList();
            _responses[respId] = (items, response, DateTime.UtcNow);
            if (_responses.Count > MaxStoredResponses)
                foreach (var old in _responses.OrderBy(x => x.Value.At).Take(_responses.Count - MaxStoredResponses)) _responses.TryRemove(old.Key, out _);
        }

        // ---------------- non-streaming
        if (!stream)
        {
            var (text, thinking) = await rt.CompleteSegmentsAsync(prompt, g, stats, ct, images);
            reasoningChars = thinking.Length;
            string answer = text;
            var calls = new List<ParsedToolCall>();
            if (tools != null) (answer, calls) = ToolStreamParser.ParseAll(tools.Format, text);
            contentChars = answer.Length;
            if (thinking.Length > 0) output.Add(ReasoningItem("rs_" + Guid.NewGuid().ToString("N")[..24], thinking));
            if (answer.Length > 0 || calls.Count == 0) output.Add(MessageItem("msg_" + Guid.NewGuid().ToString("N")[..24], answer, g.Logprobs ? LogprobsFormat.Items(stats.Logprobs.ToList()) : null));
            foreach (var c in calls) output.Add(CallItem("fc_" + Guid.NewGuid().ToString("N")[..24], c.Id, c.Name, c.Arguments));
            var final = Skeleton(stats.FinishReason == "length" ? "incomplete" : "completed");
            Remember(final);
            await WriteJson(ctx, final);
            return;
        }

        // ---------------- streaming
        SseHeaders(ctx);
        int seq = 0;
        await RespEvent(ctx, seq++, "response.created", new JsonObject { ["response"] = Skeleton("in_progress") });
        await RespEvent(ctx, seq++, "response.in_progress", new JsonObject { ["response"] = Skeleton("in_progress") });

        // one open item at a time: "reasoning", "message" or "call:<index>"
        string? open = null; string openId = ""; int outIndex = -1;
        var text2 = new StringBuilder();
        var lpAll = new JsonArray();            // log-probabilities of the answer text so far (when requested)
        JsonArray? lpPending = null;
        int lpSent = 0; var lpTrim = new LeadingTrim();
        string openCallId = "", openName = "";

        async Task CloseItem()
        {
            if (open == null) return;
            if (open == "reasoning")
            {
                string t = text2.ToString();
                await RespEvent(ctx, seq++, "response.reasoning_summary_text.done", new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["summary_index"] = 0, ["text"] = t });
                await RespEvent(ctx, seq++, "response.reasoning_summary_part.done", new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["summary_index"] = 0, ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = t } });
                var item = ReasoningItem(openId, t);
                output.Add(item);
                await RespEvent(ctx, seq++, "response.output_item.done", new JsonObject { ["output_index"] = outIndex, ["item"] = item.DeepClone() });
            }
            else if (open == "message")
            {
                string t = text2.ToString();
                var doneEv = new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["content_index"] = 0, ["text"] = t };
                if (g.Logprobs) doneEv["logprobs"] = lpAll.DeepClone();
                await RespEvent(ctx, seq++, "response.output_text.done", doneEv);
                await RespEvent(ctx, seq++, "response.content_part.done", new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["content_index"] = 0, ["part"] = OutputText(t, lpAll) });
                var item = MessageItem(openId, t, lpAll);
                output.Add(item);
                await RespEvent(ctx, seq++, "response.output_item.done", new JsonObject { ["output_index"] = outIndex, ["item"] = item.DeepClone() });
            }
            else
            {
                string args = text2.ToString();
                if (args.Length == 0) args = "{}";
                await RespEvent(ctx, seq++, "response.function_call_arguments.done", new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["arguments"] = args });
                var item = CallItem(openId, openCallId, openName, args);
                output.Add(item);
                await RespEvent(ctx, seq++, "response.output_item.done", new JsonObject { ["output_index"] = outIndex, ["item"] = item.DeepClone() });
            }
            open = null; text2.Clear();
        }

        async Task OpenItem(string kind, string prefix)
        {
            await CloseItem();
            outIndex++;
            open = kind;
            openId = prefix + Guid.NewGuid().ToString("N")[..24];
            if (kind == "reasoning")
            {
                await RespEvent(ctx, seq++, "response.output_item.added", new JsonObject { ["output_index"] = outIndex, ["item"] = new JsonObject { ["id"] = openId, ["type"] = "reasoning", ["summary"] = new JsonArray() } });
                await RespEvent(ctx, seq++, "response.reasoning_summary_part.added", new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["summary_index"] = 0, ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = "" } });
            }
            else if (kind == "message")
            {
                await RespEvent(ctx, seq++, "response.output_item.added", new JsonObject { ["output_index"] = outIndex, ["item"] = new JsonObject { ["id"] = openId, ["type"] = "message", ["status"] = "in_progress", ["role"] = "assistant", ["content"] = new JsonArray() } });
                await RespEvent(ctx, seq++, "response.content_part.added", new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["content_index"] = 0, ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() } });
            }
        }

        var parser = tools != null ? new ToolStreamParser(tools.Format) : null;
        async Task Handle(ToolEvent ev)
        {
            switch (ev)
            {
                case ContentEvent c:
                    if (open != "message") await OpenItem("message", "msg_");
                    text2.Append(c.Text); contentChars += c.Text.Length;
                    var deltaEv = new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["content_index"] = 0, ["delta"] = c.Text };
                    if (g.Logprobs)
                    {
                        deltaEv["logprobs"] = lpPending?.DeepClone() ?? new JsonArray();
                        if (lpPending != null) foreach (var x in lpPending) lpAll.Add(x!.DeepClone());
                        lpPending = null;
                    }
                    await RespEvent(ctx, seq++, "response.output_text.delta", deltaEv);
                    break;
                case CallStartEvent s:
                    await CloseItem();
                    outIndex++;
                    open = "call"; openId = "fc_" + Guid.NewGuid().ToString("N")[..24]; openCallId = s.Id; openName = s.Name;
                    await RespEvent(ctx, seq++, "response.output_item.added", new JsonObject
                    {
                        ["output_index"] = outIndex,
                        ["item"] = new JsonObject { ["id"] = openId, ["type"] = "function_call", ["status"] = "in_progress", ["call_id"] = s.Id, ["name"] = s.Name, ["arguments"] = "" },
                    });
                    break;
                case ArgsDeltaEvent a:
                    text2.Append(a.Text);
                    await RespEvent(ctx, seq++, "response.function_call_arguments.delta", new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["delta"] = a.Text });
                    break;
                case CallEndEvent:
                    await CloseItem();
                    break;
            }
        }

        // tokens produced since the last answer event go with the next one
        void SetPending()
        {
            if (!g.Logprobs) return;
            List<TokenLogprob> fresh;
            lock (stats.Logprobs) { fresh = stats.Logprobs.Skip(lpSent).ToList(); lpSent += fresh.Count; }
            var items = LogprobsFormat.Items(fresh, lpTrim);
            if (lpPending == null) lpPending = items; else foreach (var x in items) lpPending.Add(x!.DeepClone());
        }

        await foreach (var seg in rt.StreamSegmentsAsync(prompt, g, stats, ct, images))
        {
            if (seg.Kind == SegmentKind.Reasoning)
            {
                if (open != "reasoning") await OpenItem("reasoning", "rs_");
                text2.Append(seg.Text); reasoningChars += seg.Text.Length;
                await RespEvent(ctx, seq++, "response.reasoning_summary_text.delta", new JsonObject { ["item_id"] = openId, ["output_index"] = outIndex, ["summary_index"] = 0, ["delta"] = seg.Text });
            }
            else if (parser != null) { SetPending(); foreach (var ev in parser.Feed(seg.Text)) await Handle(ev); }
            else { SetPending(); await Handle(new ContentEvent(seg.Text)); }
        }
        if (parser != null) foreach (var ev in parser.Finish()) await Handle(ev);
        if (output.Count == 0 && open == null) await OpenItem("message", "msg_");
        await CloseItem();

        var done = Skeleton(stats.FinishReason == "length" ? "incomplete" : "completed");
        Remember(done);
        await RespEvent(ctx, seq++, stats.FinishReason == "length" ? "response.incomplete" : "response.completed", new JsonObject { ["response"] = done });
    }

    private static JsonObject UsageObj(GenerationStats s, int reasoningChars, int contentChars)
    {
        double share = reasoningChars + contentChars > 0 ? (double)reasoningChars / (reasoningChars + contentChars) : 0;
        return new JsonObject
        {
            ["input_tokens"] = s.PromptTokens,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = s.CachedTokens },
            ["output_tokens"] = s.CompletionTokens,
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = (int)Math.Round(s.CompletionTokens * share) },
            ["total_tokens"] = s.PromptTokens + s.CompletionTokens,
        };
    }
}
