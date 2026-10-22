using System.Text.Json;
using LLMHostProject.Core;

namespace LLMHostProject.Models;

/// <summary>
/// What is remembered about where a downloaded model came from, in <c>&lt;model file&gt;.llmhost.json</c> next to it. Files that were
/// downloaded before this existed (or imported by hand) have no sidecar and show up as "unknown" in the update check: they are not hashed
/// afterwards to guess.
/// </summary>
public record ModelSidecar(string Repo, string FileName, Dictionary<string, string?> Sha256, string? Revision, DateTime DownloadedUtc)
{
    public static string PathFor(string modelPath) => modelPath + ".llmhost.json";

    public static ModelSidecar? Read(string modelPath)
    {
        try { var p = PathFor(modelPath); return File.Exists(p) ? JsonSerializer.Deserialize<ModelSidecar>(File.ReadAllText(p), Json.Options) : null; }
        catch { return null; }
    }

    public void Write(string modelPath)
    {
        try { File.WriteAllText(PathFor(modelPath), JsonSerializer.Serialize(this, Json.Options)); } catch { }
    }
}

/// <summary>Outcome of the update check for one downloaded model. Status: current | update | missing | renamed | unknown | error.</summary>
public record UpdateStatus(string ModelId, string? Repo, string FileName, string Status, string? LocalSha, string? RemoteSha,
    string? RenamedTo, string? Message, DateTime CheckedUtc);

/// <summary>Asks Hugging Face whether the files behind the downloaded models changed (same file name, different LFS SHA-256).</summary>
public class ModelUpdateChecker
{
    private readonly ModelLibrary _library;
    private readonly object _gate = new();
    private readonly Dictionary<string, UpdateStatus> _last = new();
    public TimeSpan PauseBetweenRepos { get; set; } = TimeSpan.FromMilliseconds(300);

    public ModelUpdateChecker(ModelLibrary library) => _library = library;

    /// <summary>The newest result per model id.</summary>
    public IReadOnlyList<UpdateStatus> Last { get { lock (_gate) return _last.Values.OrderBy(s => s.ModelId).ToList(); } }

    /// <summary>Compares a sidecar with the file list of its repository. Pure, so it can be tested without a network.</summary>
    public static (string Status, string? Local, string? Remote, string? Message) Compare(ModelSidecar sidecar, IReadOnlyList<RemoteFile> remote)
    {
        var file = remote.FirstOrDefault(f => f.Shards.Contains(sidecar.FileName, StringComparer.OrdinalIgnoreCase)
                                              || string.Equals(f.FileName, sidecar.FileName, StringComparison.OrdinalIgnoreCase));
        if (file == null) return ("missing", null, null, "The file is no longer in the repository (renamed or removed).");
        string? firstShard = file.Shards.FirstOrDefault(s => Path.GetFileName(s).Equals(Path.GetFileName(sidecar.FileName), StringComparison.OrdinalIgnoreCase)) ?? file.Shards.FirstOrDefault();
        // every shard has to match; the first differing one decides
        foreach (var shard in file.Shards)
        {
            string name = Path.GetFileName(shard);
            string? local = sidecar.Sha256.FirstOrDefault(kv => Path.GetFileName(kv.Key).Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            string? remoteSha = file.Hashes?.GetValueOrDefault(shard);
            if (local == null || remoteSha == null) return ("unknown", local, remoteSha, "No checksum is available to compare.");
            if (!local.Equals(remoteSha, StringComparison.OrdinalIgnoreCase)) return ("update", local, remoteSha, "A newer version of this file is available.");
        }
        return ("current", sidecar.Sha256.GetValueOrDefault(firstShard ?? ""), file.Hashes?.GetValueOrDefault(firstShard ?? ""), null);
    }

    /// <summary>Checks one model (by library id) or all of them. Repositories are queried one at a time with a short pause.</summary>
    public async Task<IReadOnlyList<UpdateStatus>> CheckAsync(string? modelId = null, CancellationToken ct = default)
    {
        var models = _library.List().Where(m => !m.IsAdapter && (modelId == null || m.Id == modelId)).ToList();
        var byRepo = new Dictionary<string, (IReadOnlyList<RemoteFile>? Files, string? Renamed, string? Error)>(StringComparer.OrdinalIgnoreCase);
        var results = new List<UpdateStatus>();
        bool first = true;
        foreach (var m in models)
        {
            ct.ThrowIfCancellationRequested();
            var sc = ModelSidecar.Read(m.Path);
            if (sc == null)
            {
                results.Add(new(m.Id, null, m.FileName, "unknown", null, null, null, "Downloaded before updates were tracked (or imported): the origin is not known.", DateTime.UtcNow));
                continue;
            }
            if (!byRepo.TryGetValue(sc.Repo, out var info))
            {
                if (!first) await Task.Delay(PauseBetweenRepos, ct);
                first = false;
                try
                {
                    var files = await HuggingFace.ListFilesAsync(sc.Repo, ct);
                    var repoInfo = await HuggingFace.GetRepoInfoAsync(sc.Repo, ct);
                    string? renamed = repoInfo?.Id != null && !repoInfo.Id.Equals(sc.Repo, StringComparison.OrdinalIgnoreCase) ? repoInfo.Id : null;
                    info = (files, renamed, null);
                }
                catch (GatedModelException g) { info = (null, null, g.Message); }
                catch (HttpRequestException ex) { info = (null, null, ex.Message); }
                byRepo[sc.Repo] = info;
            }
            if (info.Files == null) { results.Add(new(m.Id, sc.Repo, m.FileName, "error", null, null, null, info.Error, DateTime.UtcNow)); continue; }
            var (status, local, remote, msg) = Compare(sc, info.Files);
            if (info.Renamed != null && status is "current" or "unknown") { status = "renamed"; msg = $"The repository moved to {info.Renamed}."; }
            results.Add(new(m.Id, sc.Repo, m.FileName, status, local, remote, info.Renamed, msg, DateTime.UtcNow));
        }
        lock (_gate) foreach (var r in results) _last[r.ModelId] = r;
        return results;
    }
}

public static partial class HuggingFace
{
    public record RepoInfo(string Id, string? Sha, string? LastModified);

    /// <summary>Repository id (follows renames), latest commit and date. Null when the repository cannot be read.</summary>
    public static async Task<RepoInfo?> GetRepoInfoAsync(string repo, CancellationToken ct = default)
    {
        if (!ValidRepo(repo)) return null;
        using var resp = await Http.SendAsync(Request($"https://huggingface.co/api/models/{repo}"), ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new GatedModelException(repo, HasToken);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var e = doc.RootElement;
        string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new RepoInfo(S("id") ?? repo, S("sha"), S("lastModified"));
    }
}
