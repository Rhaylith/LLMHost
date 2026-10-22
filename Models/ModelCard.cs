using System.Text.Json;
using System.Text.RegularExpressions;
using LLMHostProject.Core;

namespace LLMHostProject.Models;

/// <summary>How freely a model may be used, derived from its licence id.</summary>
/// <param name="Kind">open | restricted (custom terms or use policy) | noncommercial | unknown</param>
public record LicenseInfo(string? Id, string Kind)
{
    private static readonly string[] Open = { "apache-2.0", "mit", "bsd", "isc", "unlicense", "cc0-1.0", "cc-by-4.0", "cc-by-sa-4.0", "mpl-2.0", "wtfpl", "zlib" };

    public static LicenseInfo Classify(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return new(null, "unknown");
        string l = id.Trim().ToLowerInvariant();
        if (Regex.IsMatch(l, @"(^|[-_.])nc([-_.]|$)") || l.Contains("non-commercial") || l.Contains("noncommercial") || l.Contains("research")) return new(id, "noncommercial");
        if (Open.Any(o => l == o || l.StartsWith(o + "-"))) return new(id, "open");
        return new(id, "restricted");     // llama*, gemma, openrail, "other" ... : read the licence text
    }

    public string? Warning => Kind switch
    {
        "noncommercial" => "Non-commercial licence: commercial use is not allowed.",
        "restricted" => "Custom licence or use policy: read the terms before using it commercially.",
        _ => null,
    };
}

/// <summary>Guesses what a not-yet-downloaded model can do from its Hugging Face tags and name.</summary>
public static class RemoteCapabilities
{
    public static ModelCapabilities FromTags(IEnumerable<string>? tags, string? pipeline, string repoId)
    {
        var t = new HashSet<string>((tags ?? Array.Empty<string>()).Select(x => x.ToLowerInvariant()));
        string id = repoId.ToLowerInvariant();
        bool embedding = pipeline == "feature-extraction" || pipeline == "sentence-similarity" || t.Contains("sentence-transformers") || t.Contains("feature-extraction") || id.Contains("embed");
        bool rerank = id.Contains("rerank") || t.Contains("text-ranking");
        if (rerank) embedding = false;
        bool chat = !embedding && !rerank;
        bool vision = chat && (pipeline == "image-text-to-text" || t.Contains("image-text-to-text") || t.Contains("vision") || t.Contains("multimodal") || id.Contains("-vl") || id.Contains("vision"));
        bool audio = chat && (t.Contains("audio-text-to-text") || id.Contains("omni") || id.Contains("audio"));
        bool tools = chat && (t.Contains("function-calling") || t.Contains("tool-use") || t.Contains("tools") || id.Contains("function"));
        bool reasoning = chat && (t.Contains("reasoning") || t.Contains("thinking") || Regex.IsMatch(id, @"(qwen3|deepseek-r1|qwq|-r1|thinking|reason|gpt-oss|magistral)"));
        bool moe = t.Contains("moe") || t.Contains("mixture-of-experts") || Regex.IsMatch(id, @"(\d+b-a\d+b|moe|mixtral|8x\d+b)");
        return new ModelCapabilities(vision, audio, tools, reasoning, embedding, moe, rerank);
    }
}

/// <summary>Everything the detail drawer shows about a Hugging Face repository.</summary>
public record ModelCard(string Repo, string? License, IReadOnlyList<string> Tags, string? Pipeline, long Downloads, long Likes,
    string? LastModified, string? Gated, string? BaseModel, string Readme, DateTime FetchedUtc)
{
    [System.Text.Json.Serialization.JsonIgnore] public LicenseInfo LicenseInfo => LicenseInfo.Classify(License);
    [System.Text.Json.Serialization.JsonIgnore] public ModelCapabilities Capabilities => RemoteCapabilities.FromTags(Tags, Pipeline, Repo);
}

public static class ModelCardText
{
    /// <summary>Drops the YAML front matter (<c>---</c> block) at the start of a model README.</summary>
    public static string StripFrontMatter(string md)
    {
        md = md.TrimStart('﻿');
        var m = Regex.Match(md, @"\A---[ \t]*\r?\n.*?\r?\n---[ \t]*(\r?\n|\z)", RegexOptions.Singleline);
        return m.Success ? md[m.Length..].TrimStart() : md;
    }
}

public static partial class HuggingFace
{
    private static string CardCacheFile(string repo) =>
        Path.Combine(AppPaths.Root, "cache", "hf", Regex.Replace(repo.ToLowerInvariant(), @"[^a-z0-9._-]", "_") + ".json");

    public static bool ValidRepo(string repo) => Regex.IsMatch(repo ?? "", @"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$");

    /// <summary>Card data and README of a repository; cached for 24 hours under <c>cache/hf/</c> (a stale copy is used when the network fails).</summary>
    public static async Task<ModelCard> GetCardAsync(string repo, CancellationToken ct = default, bool refresh = false)
    {
        if (!ValidRepo(repo)) throw new ArgumentException("Repository must look like owner/name.");
        string cache = CardCacheFile(repo);
        ModelCard? old = null;
        try { if (File.Exists(cache)) old = JsonSerializer.Deserialize<ModelCard>(File.ReadAllText(cache), Json.Options); } catch { }
        if (old != null && !refresh && DateTime.UtcNow - old.FetchedUtc < TimeSpan.FromHours(24)) return old;
        try
        {
            using var resp = await Http.SendAsync(Request($"https://huggingface.co/api/models/{repo}"), ct);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new GatedModelException(repo);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var e = doc.RootElement;
            var tags = e.TryGetProperty("tags", out var tg) ? tg.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList() : new List<string>();
            string? license = tags.FirstOrDefault(x => x.StartsWith("license:"))?[8..];
            string? baseModel = tags.FirstOrDefault(x => x.StartsWith("base_model:quantized:"))?[21..]
                                ?? tags.FirstOrDefault(x => x.StartsWith("base_model:") && x.Count(c => c == ':') == 1)?[11..];
            if (e.TryGetProperty("cardData", out var cd) && cd.ValueKind == JsonValueKind.Object)
            {
                if (cd.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.String) license = lic.GetString();
                if (baseModel == null && cd.TryGetProperty("base_model", out var bm))
                    baseModel = bm.ValueKind == JsonValueKind.String ? bm.GetString() : bm.ValueKind == JsonValueKind.Array ? bm.EnumerateArray().Select(x => x.GetString()).FirstOrDefault() : null;
            }
            string readme = "";
            try
            {
                using var rr = await Http.SendAsync(Request($"https://huggingface.co/{repo}/raw/main/README.md"), ct);
                if (rr.IsSuccessStatusCode) readme = ModelCardText.StripFrontMatter(await rr.Content.ReadAsStringAsync(ct));
            }
            catch (HttpRequestException) { }
            string? gated = e.TryGetProperty("gated", out var g) ? (g.ValueKind == JsonValueKind.String ? g.GetString() : g.ValueKind == JsonValueKind.True ? "auto" : null) : null;
            var card = new ModelCard(repo, license, tags, e.TryGetProperty("pipeline_tag", out var p) ? p.GetString() : null,
                e.TryGetProperty("downloads", out var d) ? d.GetInt64() : 0, e.TryGetProperty("likes", out var l) ? l.GetInt64() : 0,
                e.TryGetProperty("lastModified", out var m) ? m.GetString() : null, gated, baseModel, readme, DateTime.UtcNow);
            try { Directory.CreateDirectory(Path.GetDirectoryName(cache)!); File.WriteAllText(cache, JsonSerializer.Serialize(card, Json.Options)); } catch { }
            return card;
        }
        catch (HttpRequestException) when (old != null) { return old; }
    }
}

/// <summary>The repository needs a signed-in account that accepted its licence (see §3.3).</summary>
public class GatedModelException : Exception
{
    public string Repo { get; }
    public string AccessUrl => $"https://huggingface.co/{Repo}";
    public GatedModelException(string repo) : base($"'{repo}' requires accepting its licence on Hugging Face.") => Repo = repo;
}
