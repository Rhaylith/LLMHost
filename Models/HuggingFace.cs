using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLMHostProject.Core;

namespace LLMHostProject.Models;

public record RemoteModel(string Id, long Downloads, long Likes, string? LastModified, string? Pipeline);

/// <summary>A downloadable GGUF (possibly split into several shards).</summary>
public record RemoteFile(string Repo, string FileName, long SizeBytes, string Quant, IReadOnlyList<string> Shards,
    string? MmprojFile = null, long MmprojSize = 0, IReadOnlyDictionary<string, string>? Hashes = null,
    IReadOnlyDictionary<string, long>? Sizes = null)
{
    public bool IsSplit => Shards.Count > 1;
    /// <summary>True when the repository ships a multimodal projector for this model (image input).</summary>
    public bool HasVision => MmprojFile != null;
    /// <summary>Model bytes plus the projector, i.e. what a download will fetch.</summary>
    public long TotalDownloadBytes => SizeBytes + MmprojSize;
}

/// <summary>What a user typed into "download a model" resolved to.</summary>
public record ModelReference(string Kind, string? Repo, string? File, string? Quant, string? Url);

public static partial class HuggingFace
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("llmhost/1.0");
        return c;
    }

    public static HttpClient Client => Http;

    private static bool IsHuggingFaceHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase)
            || u.Host.Equals("hf.co", StringComparison.OrdinalIgnoreCase));

    public static HttpRequestMessage Request(string url, long? rangeFrom = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        // the token is only ever sent to Hugging Face itself, never to an arbitrary download URL
        var token = AppSettings.Current.HuggingFaceToken ?? Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrWhiteSpace(token) && IsHuggingFaceHost(url)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (rangeFrom is > 0) req.Headers.Range = new RangeHeaderValue(rangeFrom, null);
        return req;
    }

    public static async Task<IReadOnlyList<RemoteModel>> SearchAsync(string query, int limit = 25, CancellationToken ct = default)
    {
        var url = $"https://huggingface.co/api/models?search={Uri.EscapeDataString(query)}&filter=gguf&sort=downloads&direction=-1&limit={limit}";
        using var resp = await Http.SendAsync(Request(url), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var list = new List<RemoteModel>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            list.Add(new RemoteModel(
                e.GetProperty("id").GetString()!,
                e.TryGetProperty("downloads", out var d) ? d.GetInt64() : 0,
                e.TryGetProperty("likes", out var l) ? l.GetInt64() : 0,
                e.TryGetProperty("lastModified", out var m) ? m.GetString() : null,
                e.TryGetProperty("pipeline_tag", out var p) ? p.GetString() : null));
        }
        return list;
    }

    [GeneratedRegex(@"(IQ\d_[A-Z]+|Q\d_K_[SML]|Q\d_K|Q\d_[01]|MXFP4|BF16|FP16|F16|F32)", RegexOptions.IgnoreCase)]
    private static partial Regex QuantRegex();

    [GeneratedRegex(@"-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex ShardRegex();

    private record RepoFile(string Path, long Size, string? Sha256);

    public static async Task<IReadOnlyList<RemoteFile>> ListFilesAsync(string repo, CancellationToken ct = default)
    {
        var files = new List<RepoFile>();
        await CollectAsync($"https://huggingface.co/api/models/{repo}/tree/main", files, ct);

        var result = new List<RemoteFile>();
        var mmprojs = files.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                                       && System.IO.Path.GetFileName(f.Path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var hashes = files.Where(f => f.Sha256 != null).ToDictionary(f => f.Path, f => f.Sha256!);
        var sizes = files.GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.First().Size);
        var shardGroups = new Dictionary<string, List<RepoFile>>();
        foreach (var f in files)
        {
            string path = f.Path;
            if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            if (System.IO.Path.GetFileName(path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
            var sm = ShardRegex().Match(path);
            if (sm.Success)
            {
                string key = path[..sm.Index];
                if (!shardGroups.TryGetValue(key, out var g)) shardGroups[key] = g = new();
                g.Add(f);
            }
            else
            {
                result.Add(new RemoteFile(repo, path, f.Size, ParseQuant(path), new[] { path }, Hashes: hashes, Sizes: sizes));
            }
        }
        foreach (var (key, g) in shardGroups)
        {
            var ordered = g.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
            result.Add(new RemoteFile(repo, ordered[0].Path, ordered.Sum(x => x.Size), ParseQuant(key),
                ordered.Select(x => x.Path).ToList(), Hashes: hashes, Sizes: sizes));
        }
        // attach the best multimodal projector (prefer F16/BF16 quality, then Q8_0, then whatever is there)
        var best = mmprojs.OrderBy(m => MmprojRank(m.Path)).ThenBy(m => m.Size).FirstOrDefault();
        if (best != null)
            result = result.Select(f => f with { MmprojFile = best.Path, MmprojSize = best.Size }).ToList();
        return result.OrderBy(f => f.SizeBytes).ToList();
    }

    private static async Task CollectAsync(string url, List<RepoFile> into, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(Request(url), ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Hugging Face returned {(int)resp.StatusCode} for {url}", null, resp.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string type = e.GetProperty("type").GetString()!;
            string path = e.GetProperty("path").GetString()!;
            if (type == "directory")
            {
                // sharded quants often live in sub-folders
                var baseUrl = url.Contains("/tree/main") ? url[..(url.IndexOf("/tree/main", StringComparison.Ordinal) + 10)] : url;
                await CollectAsync($"{baseUrl}/{path}", into, ct);
            }
            else
            {
                long size = 0;
                string? sha = null;
                if (e.TryGetProperty("lfs", out var lfs))
                {
                    if (lfs.TryGetProperty("size", out var s)) size = s.GetInt64();
                    if (lfs.TryGetProperty("oid", out var oid)) sha = oid.GetString();   // LFS object id = SHA-256 of the file
                }
                if (size == 0 && e.TryGetProperty("size", out var s2)) size = s2.GetInt64();
                into.Add(new RepoFile(path, size, sha));
            }
        }
    }

    private static int MmprojRank(string path)
    {
        var n = System.IO.Path.GetFileName(path).ToLowerInvariant();
        if (n.Contains("f16") && !n.Contains("bf16")) return 0;
        if (n.Contains("bf16")) return 1;
        if (n.Contains("q8_0")) return 2;
        return 3;
    }

    public static string ParseQuant(string fileName)
    {
        var m = QuantRegex().Match(System.IO.Path.GetFileName(fileName));
        var q = m.Success ? m.Value.ToUpperInvariant() : "?";
        return q == "FP16" ? "F16" : q;
    }

    public static string ResolveUrl(string repo, string path) =>
        $"https://huggingface.co/{repo}/resolve/main/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}?download=true";

    /// <summary>
    /// Understands everything a user might paste: <c>user/model</c>, <c>user/model:Q4_K_M</c>, <c>user/model/file.gguf</c>,
    /// a huggingface.co / hf.co page link, a <c>/resolve/</c> or <c>/blob/</c> file link, or any direct .gguf URL.
    /// </summary>
    public static ModelReference ParseReference(string input)
    {
        string s = (input ?? "").Trim().Trim('"', '\'');
        if (s.Length == 0) throw new ArgumentException("Nothing to download: enter a repository, a Hugging Face link or a .gguf URL.");

        if (s.StartsWith("hf://", StringComparison.OrdinalIgnoreCase)) s = "https://huggingface.co/" + s[5..];
        else if (s.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase) || s.StartsWith("huggingface.co/", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s;

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
            if (IsHuggingFaceHost(s))
            {
                if (segs.Count >= 1 && segs[0] == "models") segs.RemoveAt(0);
                if (segs.Count < 2) throw new ArgumentException("That Hugging Face link has no repository in it.");
                string repo = segs[0] + "/" + segs[1];
                if (segs.Count >= 5 && segs[2] is "resolve" or "blob" or "raw")
                {
                    string file = string.Join('/', segs.Skip(4));
                    if (!file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Only .gguf files can be downloaded.");
                    return new ModelReference("file", repo, file, null, null);
                }
                return new ModelReference("repo", repo, null, null, null);
            }
            if (!uri.AbsolutePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The URL must point to a .gguf file (or be a Hugging Face model link).");
            return new ModelReference("url", null, null, null, s);
        }

        // user/model[:quant]  or  user/model/path/to/file.gguf
        string? quant = null;
        int colon = s.LastIndexOf(':');
        if (colon > 0 && colon < s.Length - 1 && !s[(colon + 1)..].Contains('/') && !s[(colon + 1)..].Contains('\\'))
        {
            quant = s[(colon + 1)..];
            s = s[..colon];
        }
        var parts = s.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException($"'{input}' is not a model reference. Use user/model, a Hugging Face link, or a .gguf URL.");
        string r = parts[0] + "/" + parts[1];
        if (parts.Length > 2)
        {
            string file = string.Join('/', parts.Skip(2));
            if (!file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only .gguf files can be downloaded.");
            return new ModelReference("file", r, file, null, null);
        }
        return new ModelReference("repo", r, null, quant, null);
    }
}
