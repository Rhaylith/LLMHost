using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMHostProject.Core;

/// <summary>One tool offered by an MCP server.</summary>
public record McpTool(string Server, string Name, string Description, JsonNode? InputSchema)
{
    /// <summary>Name the model sees: unique across servers, only characters models accept.</summary>
    public string QualifiedName => McpManager.Sanitize(Server) + "__" + McpManager.Sanitize(Name);
}

public record McpToolResult(bool IsError, string Text, JsonNode? Raw);

/// <summary>Connection to one MCP server over stdio (local process) or streamable HTTP (remote URL).</summary>
public sealed class McpClient : IDisposable
{
    private const string ProtocolVersion = "2025-03-26";
    private readonly string _name;
    private readonly JsonObject _cfg;
    private Process? _proc;
    private HttpClient? _http;
    private string? _sessionId;
    private int _nextId;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private Task? _reader;

    public string Name => _name;
    public string Transport => _cfg["url"] != null ? "remote" : "stdio";
    public string State { get; private set; } = "idle";      // idle | starting | ready | failed
    public string? Error { get; private set; }
    public string? ServerName { get; private set; }
    public List<McpTool> Tools { get; private set; } = new();

    public McpClient(string name, JsonObject cfg) { _name = name; _cfg = cfg; }

    // ------------------------------------------------------------------ connect

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        if (State == "ready") return;
        await _connectGate.WaitAsync(ct);
        try
        {
            if (State == "ready") return;
            State = "starting"; Error = null;
            try
            {
                if (_cfg["url"] != null) StartHttp(); else StartProcess();
                var init = await RequestAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "llmhost", ["version"] = "1.0" },
                }, TimeSpan.FromSeconds(60), ct);
                ServerName = init["serverInfo"]?["name"]?.GetValue<string>();
                await NotifyAsync("notifications/initialized", null, ct);
                Tools = await ListToolsAsync(ct);
                State = "ready";
                LogBuffer.Add("mcp", $"{_name}: connected ({ServerName ?? Transport}), {Tools.Count} tool(s)");
            }
            catch (Exception ex)
            {
                State = "failed"; Error = ex.Message;
                LogBuffer.Add("mcp", $"{_name}: {ex.Message}");
                Close();
                throw;
            }
        }
        finally { _connectGate.Release(); }
    }

    private void StartProcess()
    {
        string command = _cfg["command"]?.GetValue<string>() ?? throw new InvalidOperationException("The server needs a 'command' or a 'url'.");
        var args = (_cfg["args"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? "").ToList() ?? new List<string>();
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        // npx / npm / uvx ... are .cmd shims on Windows: run them through the command interpreter
        if (command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(command) && File.Exists(command)
            && !command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = command;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        if (_cfg["cwd"]?.GetValue<string>() is { Length: > 0 } cwd) psi.WorkingDirectory = cwd;
        if (_cfg["env"] is JsonObject env)
            foreach (var (k, v) in env) psi.Environment[k] = v?.GetValue<string>() ?? "";

        _proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{command}'.");
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LogBuffer.Add("mcp", $"{_name}: {e.Data}"); };
        _proc.BeginErrorReadLine();
        _reader = Task.Run(ReadLoopAsync);
    }

    private void StartHttp()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("llmhost-mcp/1.0");
        if (_cfg["headers"] is JsonObject h)
            foreach (var (k, v) in h) _http.DefaultRequestHeaders.TryAddWithoutValidation(k, v?.GetValue<string>() ?? "");
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while (_proc != null && (line = await _proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                try { Dispatch(JsonNode.Parse(line)); } catch { /* not a JSON-RPC line (log noise) */ }
            }
        }
        catch { }
        // the process ended: fail everything that is still waiting
        FailPending(new IOException("The MCP server process exited."));
        if (State == "ready") State = "idle";
    }

    private void Dispatch(JsonNode? msg)
    {
        if (msg is not JsonObject o) return;
        if (o["id"] is JsonValue idv && o["method"] == null && idv.TryGetValue<int>(out var id))
        {
            TaskCompletionSource<JsonNode>? tcs;
            lock (_lock) { _pending.Remove(id, out tcs); }
            if (tcs == null) return;
            if (o["error"] is JsonObject err) tcs.TrySetException(new InvalidOperationException(err["message"]?.GetValue<string>() ?? "MCP error"));
            else tcs.TrySetResult(o["result"] ?? new JsonObject());
        }
        // requests / notifications from the server (sampling, logging, ...) are not supported and ignored
    }

    private void FailPending(Exception ex)
    {
        List<TaskCompletionSource<JsonNode>> all;
        lock (_lock) { all = _pending.Values.ToList(); _pending.Clear(); }
        foreach (var t in all) t.TrySetException(ex);
    }

    // ------------------------------------------------------------------ JSON-RPC

    private async Task<JsonNode> RequestAsync(string method, JsonNode? args, TimeSpan timeout, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextId);
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (args != null) msg["params"] = args;

        if (_http != null) return await HttpRequestAsync(id, msg, timeout, ct);

        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _pending[id] = tcs;
        await SendLineAsync(msg.ToJsonString(), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using (cts.Token.Register(() => tcs.TrySetCanceled()))
        {
            try { return await tcs.Task; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException($"MCP request '{method}' timed out after {timeout.TotalSeconds:0}s."); }
        }
    }

    private async Task NotifyAsync(string method, JsonNode? args, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (args != null) msg["params"] = args;
        if (_http != null) { await HttpPostAsync(msg, ct, expectResponse: false); return; }
        await SendLineAsync(msg.ToJsonString(), ct);
    }

    private async Task SendLineAsync(string line, CancellationToken ct)
    {
        if (_proc == null || _proc.HasExited) throw new IOException("The MCP server process is not running.");
        await _writeGate.WaitAsync(ct);
        try
        {
            await _proc.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await _proc.StandardInput.FlushAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    private async Task<HttpResponseMessage> HttpPostAsync(JsonNode msg, CancellationToken ct, bool expectResponse = true)
    {
        string url = _cfg["url"]!.GetValue<string>();
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (_sessionId != null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        var resp = await _http!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _sessionId = sid.FirstOrDefault();
        if (!resp.IsSuccessStatusCode && expectResponse)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"MCP server returned {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
        }
        return resp;
    }

    private async Task<JsonNode> HttpRequestAsync(int id, JsonObject msg, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var resp = await HttpPostAsync(msg, cts.Token);
        string type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("text/event-stream"))
        {
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
            var data = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                if (line.StartsWith("data:")) data.Append(line[5..].TrimStart());
                else if (line.Length == 0 && data.Length > 0)
                {
                    var node = SafeParse(data.ToString()); data.Clear();
                    if (node is JsonObject o && o["id"]?.GetValue<int>() == id) return Unwrap(o);
                }
            }
            throw new IOException("The MCP server closed the stream without answering.");
        }
        string text = await resp.Content.ReadAsStringAsync(cts.Token);
        var parsed = SafeParse(text);
        if (parsed is JsonArray arr) parsed = arr.FirstOrDefault(x => x?["id"]?.GetValue<int>() == id);
        if (parsed is not JsonObject obj) throw new InvalidDataException("The MCP server sent an unreadable answer.");
        return Unwrap(obj);
    }

    private static JsonNode? SafeParse(string s) { try { return JsonNode.Parse(s); } catch { return null; } }

    private static JsonNode Unwrap(JsonObject o)
    {
        if (o["error"] is JsonObject err) throw new InvalidOperationException(err["message"]?.GetValue<string>() ?? "MCP error");
        return o["result"] ?? new JsonObject();
    }

    // ------------------------------------------------------------------ MCP methods

    private async Task<List<McpTool>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        do
        {
            var args = cursor != null ? new JsonObject { ["cursor"] = cursor } : null;
            var r = await RequestAsync("tools/list", args, TimeSpan.FromSeconds(30), ct);
            foreach (var t in r["tools"] as JsonArray ?? new JsonArray())
            {
                string? n = t?["name"]?.GetValue<string>();
                if (n == null) continue;
                tools.Add(new McpTool(_name, n, t?["description"]?.GetValue<string>() ?? "", t?["inputSchema"]?.DeepClone()));
            }
            cursor = r["nextCursor"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(cursor));
        return tools;
    }

    public async Task<McpToolResult> CallAsync(string tool, JsonNode? arguments, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        var r = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = tool, ["arguments"] = arguments?.DeepClone() ?? new JsonObject(),
        }, TimeSpan.FromMinutes(2), ct);
        var sb = new StringBuilder();
        foreach (var c in r["content"] as JsonArray ?? new JsonArray())
        {
            switch (c?["type"]?.GetValue<string>())
            {
                case "text": sb.AppendLine(c["text"]?.GetValue<string>()); break;
                case "image": sb.AppendLine($"[image: {c["mimeType"]?.GetValue<string>()}]"); break;
                case "resource": sb.AppendLine(c["resource"]?["text"]?.GetValue<string>() ?? $"[resource {c["resource"]?["uri"]?.GetValue<string>()}]"); break;
                default: sb.AppendLine(c?.ToJsonString()); break;
            }
        }
        if (sb.Length == 0 && r["structuredContent"] != null) sb.Append(r["structuredContent"]!.ToJsonString());
        return new McpToolResult(r["isError"]?.GetValue<bool>() ?? false, sb.ToString().TrimEnd(), r);
    }

    private void Close()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        _http?.Dispose(); _http = null; _sessionId = null;
        FailPending(new IOException("The MCP connection was closed."));
    }

    public void Dispose() { Close(); State = "idle"; }
}

/// <summary>
/// The MCP servers of <c>mcp.json</c> (Cursor notation: <c>{"mcpServers": {"name": {command,args,env} | {url,headers}}}</c>).
/// Servers start on first use.
/// </summary>
public sealed class McpManager : IDisposable
{
    private readonly object _gate = new();
    private Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedStamp;

    public static string ConfigPath => Path.Combine(AppPaths.Root, "mcp.json");

    public static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

    public string ReadConfigText()
    {
        if (File.Exists(ConfigPath)) return File.ReadAllText(ConfigPath);
        return "{\n  \"mcpServers\": {}\n}\n";
    }

    public void WriteConfigText(string text)
    {
        var node = JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("mcp.json must be a JSON object.");
        if (node["mcpServers"] is not JsonObject) throw new ArgumentException("mcp.json needs an \"mcpServers\" object.");
        AppPaths.EnsureCreated();
        File.WriteAllText(ConfigPath, text);
        Reload();
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            var stamp = File.Exists(ConfigPath) ? File.GetLastWriteTimeUtc(ConfigPath) : DateTime.MinValue;
            if (stamp == _loadedStamp) return;
            foreach (var c in _clients.Values) c.Dispose();
            _clients = new Dictionary<string, McpClient>(StringComparer.OrdinalIgnoreCase);
            _loadedStamp = stamp;
            try
            {
                if (File.Exists(ConfigPath) && JsonNode.Parse(File.ReadAllText(ConfigPath)) is JsonObject root
                    && root["mcpServers"] is JsonObject servers)
                    foreach (var (name, cfg) in servers)
                        if (cfg is JsonObject o && o["disabled"]?.GetValue<bool>() != true) _clients[name] = new McpClient(name, (JsonObject)o.DeepClone());
            }
            catch (Exception ex) { LogBuffer.Add("mcp", $"mcp.json could not be read: {ex.Message}"); }
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
            _loadedStamp = DateTime.MinValue;
        }
        EnsureLoaded();
    }

    public IReadOnlyList<McpClient> Servers { get { EnsureLoaded(); lock (_gate) return _clients.Values.ToList(); } }

    public McpClient? Get(string name) { EnsureLoaded(); lock (_gate) return _clients.GetValueOrDefault(name); }

    /// <summary>Connects every configured server (errors are kept on the server, not thrown) and returns all tools.</summary>
    public async Task<List<McpTool>> AllToolsAsync(CancellationToken ct = default, IEnumerable<string>? onlyServers = null)
    {
        var only = onlyServers?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<McpTool>();
        await Task.WhenAll(Servers.Where(s => only == null || only.Contains(s.Name)).Select(async s =>
        {
            try { await s.EnsureReadyAsync(ct); } catch { /* state/error stay on the client */ }
        }));
        foreach (var s in Servers.Where(s => only == null || only.Contains(s.Name))) list.AddRange(s.Tools);
        return list;
    }

    /// <summary>Finds the tool a model-visible (qualified) name refers to.</summary>
    public McpTool? FindTool(string qualified) =>
        Servers.SelectMany(s => s.Tools).FirstOrDefault(t => t.QualifiedName.Equals(qualified, StringComparison.OrdinalIgnoreCase));

    public async Task<McpToolResult> CallAsync(string server, string tool, JsonNode? args, CancellationToken ct = default)
    {
        var c = Get(server) ?? throw new FileNotFoundException($"No MCP server '{server}' in mcp.json.");
        return await c.CallAsync(tool, args, ct);
    }

    /// <summary>OpenAI-style function definitions for tools (name qualified with the server).</summary>
    public static JsonArray ToOpenAiTools(IEnumerable<McpTool> tools) =>
        new(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.QualifiedName,
                ["description"] = t.Description,
                ["parameters"] = t.InputSchema?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            },
        }).ToArray());

    public void Dispose()
    {
        lock (_gate) { foreach (var c in _clients.Values) c.Dispose(); _clients.Clear(); }
    }
}
