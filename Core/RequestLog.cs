using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace LLMHostProject.Core;

/// <summary>One API request as the log shows it. Bodies are kept only when <c>logging.logBodies</c> is on (redacted, 8 KB each).</summary>
public class RequestEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public DateTime Time { get; init; } = DateTime.Now;
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int Status { get; set; }
    public double DurationMs { get; set; }
    public string? Model { get; set; }
    /// <summary>Who called: the token's name, "ui" for the app's own window, "key" for the single API key, null when no token was needed.</summary>
    public string? Token { get; set; }
    public string? Ip { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public bool Stream { get; set; }
    public string? Error { get; set; }
    public string? RequestBody { get; set; }
    public string? ResponseBody { get; set; }

    public void AddTokens(int input, int output)
    {
        Interlocked.Add(ref _in, input); Interlocked.Add(ref _out, output);
        InputTokens = _in; OutputTokens = _out;
    }
    private int _in, _out;
}

/// <summary>Ring buffer of recent API requests with filtering and a live feed (the Requests view, <c>/api/requests</c>, the CLI).</summary>
public static class RequestLog
{
    public const int BodyLimit = 8 * 1024;
    private static readonly object Gate = new();
    private static readonly LinkedList<RequestEntry> Entries = new();
    private static int _capacity = 2000;

    /// <summary>The request being served on this async flow; the generation code adds its token counts to it.</summary>
    public static readonly AsyncLocal<RequestEntry?> Current = new();
    public static event Action<RequestEntry>? Added;

    public static int Capacity { get => _capacity; set => _capacity = Math.Clamp(value, 10, 100_000); }

    public static void Add(RequestEntry e)
    {
        lock (Gate)
        {
            Entries.AddLast(e);
            while (Entries.Count > _capacity) Entries.RemoveFirst();
        }
        Metrics.Observe(e);
        try { Added?.Invoke(e); } catch { }
    }

    public static void Clear() { lock (Gate) Entries.Clear(); }
    public static int Count { get { lock (Gate) return Entries.Count; } }

    public static RequestEntry? Get(string id) { lock (Gate) return Entries.FirstOrDefault(e => e.Id == id); }

    /// <summary>Newest first. <paramref name="status"/> is an exact code, a class ("4xx") or "error" (400 and above).</summary>
    public static List<RequestEntry> Query(string? model = null, string? status = null, DateTime? since = null, string? text = null, int limit = 200)
    {
        lock (Gate)
        {
            IEnumerable<RequestEntry> q = Entries.Reverse();
            if (!string.IsNullOrEmpty(model)) q = q.Where(e => e.Model?.Contains(model, StringComparison.OrdinalIgnoreCase) == true);
            if (!string.IsNullOrEmpty(status)) q = q.Where(e => StatusMatches(e.Status, status));
            if (since.HasValue) q = q.Where(e => e.Time >= since.Value);
            if (!string.IsNullOrEmpty(text))
                q = q.Where(e => e.Path.Contains(text, StringComparison.OrdinalIgnoreCase) || (e.Model?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                                 || (e.Error?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || (e.Token?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                                 || (e.RequestBody?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
            return q.Take(Math.Clamp(limit, 1, 5000)).ToList();
        }
    }

    public static bool StatusMatches(int status, string filter)
    {
        filter = filter.Trim().ToLowerInvariant();
        if (filter == "error") return status >= 400;
        if (filter.Length == 3 && filter.EndsWith("xx") && char.IsDigit(filter[0])) return status / 100 == filter[0] - '0';
        return int.TryParse(filter, out var n) && status == n;
    }

    /// <summary>Text for a log: redacted and cut to 8 KB.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string t = LogRedactor.Redact(text);
        return t.Length > BodyLimit ? t[..BodyLimit] + $"… [{text.Length - BodyLimit} more characters]" : t;
    }

    /// <summary>The route of a path without ids, so that counts group sensibly (<c>/api/chats/abc</c> → <c>/api/chats/{id}</c>).</summary>
    public static string Route(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0] == "api" && parts[1] is "chats" or "presets" or "documents" or "chat-folders") parts[2] = "{id}";
        if (parts.Length >= 3 && parts[0] == "v1" && parts[1] == "models") parts[2] = "{id}";
        return "/" + string.Join("/", parts);
    }
}

/// <summary>Counters for the Prometheus endpoint; the request log feeds them, the exporter reads them.</summary>
public static class Metrics
{
    public static readonly double[] Buckets = { 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120 };
    private static readonly ConcurrentDictionary<(string Route, int Status), long> Requests = new();
    private static readonly ConcurrentDictionary<string, long[]> Durations = new();           // route -> bucket counts (+Inf last), sum is kept apart
    private static readonly ConcurrentDictionary<string, double> DurationSum = new();
    private static readonly ConcurrentDictionary<string, long> Generated = new(), Prompted = new();
    private static readonly ConcurrentDictionary<string, double> LastTps = new();

    public static void Observe(RequestEntry e)
    {
        string route = RequestLog.Route(e.Path);
        Requests.AddOrUpdate((route, e.Status), 1, (_, v) => v + 1);
        var d = Durations.GetOrAdd(route, _ => new long[Buckets.Length + 1]);
        double sec = e.DurationMs / 1000.0;
        int i = Array.FindIndex(Buckets, b => sec <= b); if (i < 0) i = Buckets.Length;
        lock (d) { d[i]++; }
        DurationSum.AddOrUpdate(route, sec, (_, v) => v + sec);
        if (!string.IsNullOrEmpty(e.Model))
        {
            Generated.AddOrUpdate(e.Model, e.OutputTokens, (_, v) => v + e.OutputTokens);
            Prompted.AddOrUpdate(e.Model, e.InputTokens, (_, v) => v + e.InputTokens);
            if (e.OutputTokens > 1 && e.DurationMs > 0) LastTps[e.Model] = e.OutputTokens / (e.DurationMs / 1000.0);
        }
    }

    public static void Reset() { Requests.Clear(); Durations.Clear(); DurationSum.Clear(); Generated.Clear(); Prompted.Clear(); LastTps.Clear(); }

    private static string L(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The metrics in Prometheus text format. <paramref name="models"/> and <paramref name="gpus"/> carry the live gauges.</summary>
    public static string Render(IEnumerable<(string Model, int Active, int Queued, int CachedTokens)> models, IEnumerable<(string Gpu, long Bytes)> gpus)
    {
        var sb = new StringBuilder();
        void Head(string name, string help, string type) => sb.Append("# HELP ").Append(name).Append(' ').AppendLine(help).Append("# TYPE ").Append(name).Append(' ').AppendLine(type);

        Head("llmhost_requests_total", "API requests by route and status.", "counter");
        foreach (var kv in Requests.OrderBy(k => k.Key.Route).ThenBy(k => k.Key.Status))
            sb.Append($"llmhost_requests_total{{route=\"{L(kv.Key.Route)}\",status=\"{kv.Key.Status}\"}} {kv.Value}\n");

        Head("llmhost_request_duration_seconds", "Request duration.", "histogram");
        foreach (var kv in Durations.OrderBy(k => k.Key))
        {
            long cum = 0;
            long[] c; lock (kv.Value) c = (long[])kv.Value.Clone();
            for (int i = 0; i < Buckets.Length; i++) { cum += c[i]; sb.Append($"llmhost_request_duration_seconds_bucket{{route=\"{L(kv.Key)}\",le=\"{N(Buckets[i])}\"}} {cum}\n"); }
            cum += c[Buckets.Length];
            sb.Append($"llmhost_request_duration_seconds_bucket{{route=\"{L(kv.Key)}\",le=\"+Inf\"}} {cum}\n");
            sb.Append($"llmhost_request_duration_seconds_sum{{route=\"{L(kv.Key)}\"}} {N(DurationSum.GetValueOrDefault(kv.Key))}\n");
            sb.Append($"llmhost_request_duration_seconds_count{{route=\"{L(kv.Key)}\"}} {cum}\n");
        }

        Head("llmhost_tokens_generated_total", "Completion tokens generated.", "counter");
        foreach (var kv in Generated.OrderBy(k => k.Key)) sb.Append($"llmhost_tokens_generated_total{{model=\"{L(kv.Key)}\"}} {kv.Value}\n");
        Head("llmhost_prompt_tokens_total", "Prompt tokens processed.", "counter");
        foreach (var kv in Prompted.OrderBy(k => k.Key)) sb.Append($"llmhost_prompt_tokens_total{{model=\"{L(kv.Key)}\"}} {kv.Value}\n");
        Head("llmhost_tokens_per_second", "Tokens per second of the latest request that generated text.", "gauge");
        foreach (var kv in LastTps.OrderBy(k => k.Key)) sb.Append($"llmhost_tokens_per_second{{model=\"{L(kv.Key)}\"}} {N(kv.Value)}\n");

        var list = models.ToList();
        Head("llmhost_loaded_models", "Models currently loaded.", "gauge");
        sb.Append($"llmhost_loaded_models {list.Count}\n");
        Head("llmhost_active_requests", "Requests being generated.", "gauge");
        foreach (var m in list) sb.Append($"llmhost_active_requests{{model=\"{L(m.Model)}\"}} {m.Active}\n");
        Head("llmhost_queue_depth", "Requests waiting for a free slot.", "gauge");
        foreach (var m in list) sb.Append($"llmhost_queue_depth{{model=\"{L(m.Model)}\"}} {m.Queued}\n");
        Head("llmhost_kv_cache_tokens", "Tokens held in the prompt cache.", "gauge");
        foreach (var m in list) sb.Append($"llmhost_kv_cache_tokens{{model=\"{L(m.Model)}\"}} {m.CachedTokens}\n");
        Head("llmhost_gpu_memory_bytes", "Dedicated GPU memory in use by this process.", "gauge");
        foreach (var g in gpus) sb.Append($"llmhost_gpu_memory_bytes{{gpu=\"{L(g.Gpu)}\"}} {g.Bytes}\n");
        return sb.ToString();
    }
}

/// <summary>Passes writes through and keeps the first 8 KB, for the response body of the request log.</summary>
public sealed class CaptureStream : Stream
{
    private readonly Stream _inner;
    private readonly MemoryStream _copy = new();
    public CaptureStream(Stream inner) => _inner = inner;
    public string Text() => Encoding.UTF8.GetString(_copy.ToArray());
    private void Keep(ReadOnlySpan<byte> b) { int room = RequestLog.BodyLimit * 2 - (int)_copy.Length; if (room > 0) _copy.Write(b[..Math.Min(room, b.Length)]); }
    public override void Write(byte[] buffer, int offset, int count) { Keep(buffer.AsSpan(offset, count)); _inner.Write(buffer, offset, count); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Keep(buffer.Span); await _inner.WriteAsync(buffer, ct); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override void Flush() => _inner.Flush();
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
}
