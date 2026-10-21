using System.Text.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LLMHostProject.Server;

/// <summary>MCP host endpoints (/api/mcp/*) used by the web UI, and MCP tool execution for API integrations.</summary>
public partial class ApiServer
{
    private void MapMcp(WebApplication app)
    {
        object ServerDto(McpClient s) => new
        {
            s.Name, s.Transport, s.State, s.Error, serverName = s.ServerName,
            tools = s.Tools.Select(t => new { t.Name, qualified = t.QualifiedName, t.Description, schema = t.InputSchema }),
        };

        app.MapGet("/api/mcp", async ctx =>
        {
            // connect lazily on demand: ?connect=1 starts every configured server first
            if (Q(ctx, "connect") == "1") await _host.Mcp.AllToolsAsync(ctx.RequestAborted);
            await WriteJson(ctx, new { path = McpManager.ConfigPath, config = _host.Mcp.ReadConfigText(), servers = _host.Mcp.Servers.Select(ServerDto) });
        });

        app.MapPut("/api/mcp", async ctx =>
        {
            var body = await ReadBody(ctx);
            string text = body["config"]?.GetValue<string>() ?? throw new ArgumentException("'config' (the mcp.json text) is required");
            _host.Mcp.WriteConfigText(text);
            await WriteJson(ctx, new { ok = true, servers = _host.Mcp.Servers.Select(ServerDto) });
        });

        app.MapPost("/api/mcp/reload", async ctx =>
        {
            _host.Mcp.Reload();
            await WriteJson(ctx, new { servers = _host.Mcp.Servers.Select(ServerDto) });
        });

        // tools of all (or the listed) servers as OpenAI function definitions, ready to send with a chat request
        app.MapGet("/api/mcp/tools", async ctx =>
        {
            var only = Q(ctx, "servers") is { Length: > 0 } s ? s.Split(',') : null;
            var tools = await _host.Mcp.AllToolsAsync(ctx.RequestAborted, only);
            await WriteJson(ctx, new
            {
                tools = McpManager.ToOpenAiTools(tools),
                errors = _host.Mcp.Servers.Where(x => x.State == "failed").Select(x => new { x.Name, x.Error }),
            });
        });

        // execute one tool call (the web UI asks the user first, then calls this)
        app.MapPost("/api/mcp/call", async ctx =>
        {
            var body = await ReadBody(ctx);
            McpTool? tool = null;
            if (body["name"]?.GetValue<string>() is { Length: > 0 } q)
            {
                await _host.Mcp.AllToolsAsync(ctx.RequestAborted);
                tool = _host.Mcp.FindTool(q);
            }
            string server = tool?.Server ?? body["server"]?.GetValue<string>() ?? throw new ArgumentException("'name' or 'server' + 'tool' is required");
            string toolName = tool?.Name ?? body["tool"]?.GetValue<string>() ?? throw new ArgumentException("'tool' is required");
            JsonNode? args = body["arguments"];
            if (args is JsonValue av && av.TryGetValue<string>(out var astr)) { try { args = JsonNode.Parse(astr); } catch { args = new JsonObject(); } }
            var r = await _host.Mcp.CallAsync(server, toolName, args, ctx.RequestAborted);
            LogBuffer.Add("mcp", $"called {server}/{toolName}" + (r.IsError ? " (error result)" : ""));
            await WriteJson(ctx, new { server, tool = toolName, isError = r.IsError, content = r.Text, images = (r.Images ?? Array.Empty<byte[]>()).Select(b => "data:image/png;base64," + Convert.ToBase64String(b)) });
        });
    }

    // ---------------------------------------------------------------- integrations of /api/v1/chat

    /// <summary>One tool a request may use, with the client that runs it.</summary>
    private sealed record IntegrationTool(string Qualified, string ServerLabel, string Tool, McpClient Client, string ProviderType, string ProviderId);

    /// <summary>
    /// Resolves the "integrations" of a native chat request: {type: "plugin", id: "mcp/&lt;server&gt;", allowed_tools}
    /// for servers of mcp.json, and {type: "ephemeral_mcp", server_label, server_url, headers, allowed_tools} for a
    /// remote server that exists only for this request.
    /// </summary>
    private async Task<(JsonArray Tools, Dictionary<string, IntegrationTool> Map, List<McpClient> Temporary)> ResolveIntegrations(
        JsonArray integrations, CancellationToken ct)
    {
        var tools = new List<McpTool>();
        var map = new Dictionary<string, IntegrationTool>(StringComparer.OrdinalIgnoreCase);
        var temp = new List<McpClient>();
        int n = 0;
        foreach (var it in integrations)
        {
            if (it is not JsonObject o) continue;
            var allowed = (o["allowed_tools"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            McpClient client; string type, pid;
            switch (o["type"]?.GetValue<string>())
            {
                case "plugin":
                    {
                        string id = o["id"]?.GetValue<string>() ?? "";
                        string server = id.StartsWith("mcp/", StringComparison.OrdinalIgnoreCase) ? id[4..] : id;
                        client = _host.Mcp.Get(server) ?? throw new ArgumentException($"No MCP server '{server}' in mcp.json.");
                        type = "plugin"; pid = id;
                        break;
                    }
                case "ephemeral_mcp":
                    {
                        string url = o["server_url"]?.GetValue<string>() ?? throw new ArgumentException("ephemeral_mcp needs 'server_url'.");
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                            throw new ArgumentException("ephemeral_mcp 'server_url' must be an http(s) URL.");
                        var cfg = new JsonObject { ["url"] = url };
                        if (o["headers"] is JsonObject h) cfg["headers"] = h.DeepClone();
                        string label = o["server_label"]?.GetValue<string>() ?? "remote" + (++n);
                        client = new McpClient(label, cfg);
                        temp.Add(client);
                        type = "ephemeral_mcp"; pid = label;
                        break;
                    }
                default: continue;
            }
            await client.EnsureReadyAsync(ct);
            foreach (var t in client.Tools)
            {
                if (allowed != null && !allowed.Contains(t.Name)) continue;
                tools.Add(t);
                map[t.QualifiedName] = new IntegrationTool(t.QualifiedName, client.Name, t.Name, client, type, pid);
            }
        }
        return (McpManager.ToOpenAiTools(tools), map, temp);
    }
}
