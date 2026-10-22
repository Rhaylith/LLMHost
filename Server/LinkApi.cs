using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LLMHostProject.Server;

/// <summary>Linking your own devices: pairing, signed peer requests, remote models and the router that forwards requests (see <see cref="LinkManager"/>).</summary>
public partial class ApiServer
{
    private LinkManager Link => _host.Link;
    private System.Threading.Timer? _linkTimer;

    private static readonly string[] ProxiedPaths = { "/v1/chat/completions", "/v1/completions", "/v1/embeddings", "/v1/messages" };

    private LinkInfo LocalInfo()
    {
        var loaded = _host.Models.Loaded;
        var models = loaded.Select(r => new LinkModel(r.Identifier, true, r.Model.IsEmbedding || r.Options.Embeddings ? "embeddings" : r.SupportsVision ? "vlm" : "llm")).ToList();
        foreach (var m in _host.Library.List().Where(m => !loaded.Any(l => l.Model.Path == m.Path)))
            models.Add(new LinkModel(Path.GetFileNameWithoutExtension(m.FileName), false, m.IsEmbedding ? "embeddings" : m.SupportsVision ? "vlm" : "llm"));
        var (_, avail) = Hardware.MemoryStatus();
        long vram = 0;
        try { vram = HardwareMonitor.Shared.Latest(TimeSpan.FromSeconds(30)).Gpus.Select(g => Math.Max(0, g.DedicatedTotalBytes - (g.DedicatedUsedBytes ?? 0))).DefaultIfEmpty(0).Max(); } catch { }
        return new LinkInfo(Link.DeviceId, Link.DeviceName, "1", models, avail, vram);
    }

    /// <summary>Asks every paired device who it is and what it has; failures just mark it offline.</summary>
    public async Task RefreshPeersAsync(CancellationToken ct = default)
    {
        if (!Link.Enabled) return;
        using var http = LinkManager.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(8);
        await Task.WhenAll(Link.Peers.Select(async p =>
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, p.Address + "/api/link/info");
                Link.Sign(req, Array.Empty<byte>());
                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) { Link.SetStatus(p.Id, false); return; }
                var info = JsonSerializer.Deserialize<LinkInfo>(await resp.Content.ReadAsStringAsync(ct), Json.Options);
                Link.SetStatus(p.Id, true, info?.Models);
            }
            catch { Link.SetStatus(p.Id, false); }
        }));
    }

    /// <summary>
    /// For a request that came signed from a paired device: checks it and what that device may do. Returns true when the request was refused (the answer is written).
    /// The checked device is put in <c>ctx.Items["link.peer"]</c>.
    /// </summary>
    private async Task<bool> RefusedPeerAsync(HttpContext ctx)
    {
        if (!ctx.Request.Headers.ContainsKey("X-Link-Device")) return false;
        if (ctx.Request.ContentLength > 8_000_000) { await WriteError(ctx, 413, "Too large for a linked request.", "request_too_large"); return true; }
        ctx.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        ctx.Request.Body.Position = 0;
        var (peer, error) = Link.Verify(ctx.Request.Method, ctx.Request.Path.Value + ctx.Request.QueryString.Value, h => ctx.Request.Headers[h].FirstOrDefault(), ms.ToArray());
        if (peer == null)
        {
            LogBuffer.Add("link", $"Refused a linked request from {ctx.Connection.RemoteIpAddress}: {error}");
            await WriteError(ctx, 401, error ?? "Not accepted.", "link_unauthorized");
            return true;
        }
        if (!peer.Allows(ctx.Request.Method, ctx.Request.Path.Value ?? ""))
        {
            LogBuffer.Add("link", $"{peer.Name} may not call {ctx.Request.Method} {ctx.Request.Path}");
            await WriteError(ctx, 403, $"{peer.Name} does not have the permission for this.", "link_forbidden");
            return true;
        }
        ctx.Items["link.peer"] = peer;
        if (RequestLog.Current.Value is { } e) e.Token = "link:" + peer.Name;
        return false;
    }

    /// <summary>
    /// Forwards a model request to another device when the model is named "device/model" or the preferred device has it. Returns true when the
    /// answer came from there. Requests that already came from a peer are never forwarded again.
    /// </summary>
    private async Task<bool> TryProxyLinkAsync(HttpContext ctx, JsonObject body)
    {
        if (!Link.Enabled || ctx.Items.ContainsKey("link.peer") || !ProxiedPaths.Contains(ctx.Request.Path.Value)) return false;
        string? requested = body["model"]?.GetValue<string>();
        var (peer, model) = Link.SplitModel(requested);
        bool explicitPeer = peer != null;
        var s = _host.Settings.Link;
        if (peer == null && !string.IsNullOrWhiteSpace(s.PreferredDevice) && !string.IsNullOrEmpty(model) && Link.FindPeer(s.PreferredDevice) is { } pref)
        {
            if (Link.IsOnline(pref.Id) && Link.ModelsOf(pref.Id).Any(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase))) peer = pref;
            else if (!Link.IsOnline(pref.Id) && s.FailoverToLocal)
                ctx.Response.Headers["X-LLMHost-Notice"] = $"The preferred device '{pref.Name}' is offline; this answer comes from '{Link.DeviceName}'.";
        }
        if (peer == null) return false;
        if (!Link.IsOnline(peer.Id)) await RefreshPeersAsync(ctx.RequestAborted);
        if (!Link.IsOnline(peer.Id))
        {
            if (!explicitPeer && s.FailoverToLocal) { ctx.Response.Headers["X-LLMHost-Notice"] = $"The preferred device '{peer.Name}' is offline; this answer comes from '{Link.DeviceName}'."; return false; }
            await WriteError(ctx, 502, $"The device '{peer.Name}' is offline.", "link_offline");
            return true;
        }

        var forwarded = (JsonObject)body.DeepClone();
        forwarded["model"] = model;
        byte[] bytes = Encoding.UTF8.GetBytes(forwarded.ToJsonString());
        using var http = LinkManager.CreateClient();
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), peer.Address + ctx.Request.Path + ctx.Request.QueryString) { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Link.Sign(req, bytes);
        LogBuffer.Add("link", $"{ctx.Request.Path} for '{model}' goes to {peer.Name}");
        // the caller's cancellation travels with the request: closing the connection stops the generation over there
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted); }
        catch (HttpRequestException)
        {
            Link.SetStatus(peer.Id, false);
            if (!explicitPeer && s.FailoverToLocal) { ctx.Response.Headers["X-LLMHost-Notice"] = $"The preferred device '{peer.Name}' is offline; this answer comes from '{Link.DeviceName}'."; return false; }
            await WriteError(ctx, 502, $"The device '{peer.Name}' is offline.", "link_offline");
            return true;
        }
        using var _ = resp;
        ctx.Response.StatusCode = (int)resp.StatusCode;
        if (resp.Content.Headers.ContentType != null) ctx.Response.ContentType = resp.Content.Headers.ContentType.ToString();
        ctx.Response.Headers["X-LLMHost-Device"] = peer.Name;
        await using var stream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted);
        var buf = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(buf, ctx.RequestAborted)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
        return true;
    }

    /// <summary>A signed call to a peer's own API on behalf of the user (load, unload, download of a remote model).</summary>
    private async Task<IResult> CallPeerAsync(LinkPeer peer, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var http = LinkManager.CreateClient();
        byte[] bytes = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body.ToJsonString());
        var req = new HttpRequestMessage(method, peer.Address + path) { Content = body == null ? null : new ByteArrayContent(bytes) };
        if (body != null) req.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        Link.Sign(req, bytes);
        using var resp = await http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        return Results.Content(text, "application/json", Encoding.UTF8, (int)resp.StatusCode);
    }

    private void MapLink(WebApplication app)
    {
        object Status() => new
        {
            enabled = Link.Enabled, deviceId = Link.DeviceId, name = Link.DeviceName, preferredDevice = _host.Settings.Link.PreferredDevice,
            failoverToLocal = _host.Settings.Link.FailoverToLocal, peers = Link.Views(),
        };
        app.MapGet("/api/link/status", async ctx => await WriteJson(ctx, Status()));
        app.MapPost("/api/link/enable", async ctx => { _host.Settings.Link.Enabled = true; _host.Settings.Save(); await RefreshPeersAsync(); await WriteJson(ctx, Status()); });
        app.MapPost("/api/link/disable", async ctx => { _host.Settings.Link.Enabled = false; _host.Settings.Save(); await WriteJson(ctx, Status()); });
        app.MapPost("/api/link/name", async ctx =>
        {
            string name = ((await ReadBody(ctx))["name"]?.GetValue<string>() ?? "").Trim();
            if (name.Length == 0 || name.Length > 40 || name.Contains('/')) throw new ArgumentException("The device name needs 1-40 characters and no '/'.");
            _host.Settings.Link.DeviceName = name; _host.Settings.Save();
            await WriteJson(ctx, Status());
        });
        app.MapPost("/api/link/preferred", async ctx =>
        {
            string dev = ((await ReadBody(ctx))["device"]?.GetValue<string>() ?? "").Trim();
            if (dev.Length > 0 && dev != "local" && Link.FindPeer(dev) == null) throw new FileNotFoundException($"No paired device is called '{dev}'.");
            _host.Settings.Link.PreferredDevice = dev == "local" ? "" : dev; _host.Settings.Save();
            await WriteJson(ctx, Status());
        });

        // pairing: one device shows a code, the other types it
        app.MapPost("/api/link/pair/start", async ctx =>
        {
            if (!Link.Enabled) throw new InvalidOperationException("Switch linking on first.");
            await WriteJson(ctx, new { code = Link.StartPairing(), expiresInMinutes = 5, name = Link.DeviceName });
        });
        app.MapPost("/api/link/pair/accept", async ctx =>
        {
            if (!Link.Enabled) { await WriteError(ctx, 403, "Linking is switched off.", "link_off"); return; }
            var r = (await ReadBody(ctx)).Deserialize<LinkManager.PairRequest>(Json.Options);
            var reply = r == null ? null : Link.AcceptPairing(r);
            if (reply == null) { await WriteError(ctx, 403, "The code was not accepted.", "pairing_refused"); return; }
            LogBuffer.Add("link", $"Paired with {r!.Name}");
            await WriteJson(ctx, reply);
        });
        app.MapPost("/api/link/pair", async ctx =>
        {
            if (!Link.Enabled) throw new InvalidOperationException("Switch linking on first.");
            var body = await ReadBody(ctx);
            string address = body["address"]?.GetValue<string>() ?? throw new ArgumentException("'address' is required (for example http://100.64.0.7:1234).");
            if (!Uri.TryCreate(address, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) throw new ArgumentException("The address must be http:// or https://.");
            var peer = await Link.PairAsync(address, body["code"]?.GetValue<string>() ?? "", body["ownAddress"]?.GetValue<string>() ?? "", ct: ctx.RequestAborted);
            await RefreshPeersAsync(ctx.RequestAborted);
            await WriteJson(ctx, new { paired = peer.Name, id = peer.Id });
        });

        // what this device tells a peer (signed requests only reach here through the peer check in the pipeline; locally the admin may ask too)
        app.MapGet("/api/link/info", async ctx => await WriteJson(ctx, LocalInfo()));

        app.MapGet("/api/link/peers", async ctx => await WriteJson(ctx, Link.Views()));
        app.MapDelete("/api/link/peers/{id}", async ctx => await WriteJson(ctx, new { removed = Link.RemovePeer((string)ctx.Request.RouteValues["id"]!) }));
        app.MapPut("/api/link/peers/{id}/permissions", async ctx =>
        {
            var perms = ((await ReadBody(ctx))["permissions"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? new();
            if (!Link.SetPermissions((string)ctx.Request.RouteValues["id"]!, perms)) { await WriteError(ctx, 404, "No such device.", "not_found"); return; }
            await WriteJson(ctx, Link.Views());
        });
        app.MapPost("/api/link/refresh", async ctx => { await RefreshPeersAsync(ctx.RequestAborted); await WriteJson(ctx, Link.Views()); });

        // all models, grouped by device (this one first), for the model picker
        app.MapGet("/api/link/models", async ctx =>
        {
            var list = new List<object>();
            foreach (var m in LocalInfo().Models) list.Add(new { device = Link.DeviceName, local = true, id = m.Id, m.Loaded, m.Type });
            if (Link.Enabled)
                foreach (var p in Link.Peers)
                    foreach (var m in Link.ModelsOf(p.Id)) list.Add(new { device = p.Name, local = false, id = p.Name + "/" + m.Id, m.Loaded, m.Type });
            await WriteJson(ctx, list);
        });

        // explicit actions on a remote device: load, unload, download (the peer must have granted the permission)
        IResult Offline(LinkPeer? p) => Results.Json(new { error = new { message = p == null ? "No such device." : "The device is offline.", type = "link_offline" } }, statusCode: p == null ? 404 : 502);
        app.MapPost("/api/link/remote/{peer}/{action}", async ctx =>
        {
            var peer = Link.FindPeer((string)ctx.Request.RouteValues["peer"]!);
            string action = (string)ctx.Request.RouteValues["action"]!;
            if (peer == null) { await Offline(null).ExecuteAsync(ctx); return; }
            var body = await ReadBody(ctx);
            string path = action switch { "load" => "/api/models/load", "unload" => "/api/models/unload", "download" => "/api/downloads", _ => throw new ArgumentException("action must be load, unload or download.") };
            try { await (await CallPeerAsync(peer, HttpMethod.Post, path, body, ctx.RequestAborted)).ExecuteAsync(ctx); }
            catch (HttpRequestException) { await Offline(peer).ExecuteAsync(ctx); }
        });
    }
}
