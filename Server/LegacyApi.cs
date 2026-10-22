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
/// The older <c>/api/v0/*</c> routes some clients still call: thin adapters over the <c>/v1</c> handlers whose answers also carry the v0 extras
/// (<c>stats</c> with <c>stop_reason</c>, <c>model_info</c>, <c>runtime</c>). Streaming answers are passed through unchanged.
/// </summary>
public partial class ApiServer
{
    /// <summary>v0 model entry: <c>type</c> llm | vlm | embeddings, <c>state</c> loaded | not-loaded, context sizes, capabilities.</summary>
    private object V0Model(LocalModel m, ModelRuntime? loaded)
    {
        var caps = new List<string>();
        if (m.Capabilities.Tools) caps.Add("tool_use");
        if (m.Capabilities.Reasoning) caps.Add("reasoning");
        string type = m.Capabilities.Embedding || m.Capabilities.Rerank || m.IsEmbedding ? "embeddings" : m.SupportsVision ? "vlm" : "llm";
        string id = loaded?.Identifier ?? Path.GetFileNameWithoutExtension(m.FileName);
        string publisher = m.Id.Contains('/') ? m.Id.Split('/')[0] : "local";
        return new
        {
            id, @object = "model", type, publisher, arch = m.Architecture, compatibility_type = "gguf", quantization = m.Quantization,
            state = loaded != null ? "loaded" : "not-loaded", max_context_length = m.ContextLength,
            loaded_context_length = loaded != null ? (long?)loaded.ContextSize : null, capabilities = caps,
        };
    }

    private object V0Entry(string id)
    {
        var rt = _host.Models.Instances(id).FirstOrDefault();
        var m = rt?.Model ?? _host.Library.Find(id) ?? throw new FileNotFoundException($"Model '{id}' not found");
        return V0Model(m, rt);
    }

    /// <summary>Runs a /v1 handler for a v0 route; a non-streaming JSON answer gets <c>model_info</c> and <c>runtime</c> added.</summary>
    private async Task V0(HttpContext ctx, Func<HttpContext, Task> handler)
    {
        var real = ctx.Response.Body;
        await using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try { await handler(ctx); }
        finally { ctx.Response.Body = real; }
        buffer.Position = 0;
        string type = ctx.Response.ContentType ?? "";
        if (ctx.Response.StatusCode == 200 && type.Contains("json", StringComparison.OrdinalIgnoreCase) && JsonNode.Parse(buffer) is JsonObject o)
        {
            string? model = o["model"]?.GetValue<string>();
            var rt = model == null ? null : _host.Models.Instances(model).FirstOrDefault();
            if (rt != null)
            {
                o["model_info"] = JsonSerializer.SerializeToNode(V0Entry(rt.Identifier), Json.Options);
                o["runtime"] = new JsonObject { ["name"] = NativeBackend.Active, ["version"] = "llama.cpp", ["supported_formats"] = new JsonArray("gguf") };
            }
            if (o["stats"] is JsonObject st && o["choices"]?[0]?["finish_reason"]?.GetValue<string>() is { } fr) st["stop_reason"] = fr;
            ctx.Response.ContentLength = null;
            await ctx.Response.WriteAsync(o.ToJsonString(Json.Compact));
            return;
        }
        buffer.Position = 0;
        await buffer.CopyToAsync(real);
    }

    private void MapLegacy(WebApplication app)
    {
        app.MapGet("/api/v0/models", async ctx =>
        {
            var loaded = _host.Models.Loaded;
            var data = new List<object>();
            foreach (var r in loaded) data.Add(V0Model(r.Model, r));
            foreach (var m in _host.Library.List())
                if (!loaded.Any(l => l.Model.Path == m.Path)) data.Add(V0Model(m, null));
            await WriteJson(ctx, new { @object = "list", data });
        });
        app.MapGet("/api/v0/models/{*id}", async ctx =>
        {
            try { await WriteJson(ctx, V0Entry((string)ctx.Request.RouteValues["id"]!)); }
            catch (FileNotFoundException ex) { await WriteError(ctx, 404, ex.Message, "not_found"); }
        });
        app.MapPost("/api/v0/chat/completions", ctx => V0(ctx, ChatCompletions));
        app.MapPost("/api/v0/completions", ctx => V0(ctx, Completions));
        app.MapPost("/api/v0/embeddings", ctx => V0(ctx, Embeddings));
    }
}
