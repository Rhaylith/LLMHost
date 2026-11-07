using System.Collections.Concurrent;

namespace LLMHostProject.Models;

/// <summary>How big the downloadable files of a Hugging Face repository are: what the Discover lists show next to each model.</summary>
public record RepoSize(int Files, long MinBytes, long MaxBytes, long? QuantBytes, string? Quant);

public static class RepoSizes
{
    private static readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<RemoteFile> Files)> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(1);
    /// <summary>For tests: where the file list comes from.</summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<RemoteFile>>>? Lister { get; set; }

    public static RepoSize Summarize(IReadOnlyList<RemoteFile> files, string? quant = null)
    {
        if (files.Count == 0) return new RepoSize(0, 0, 0, null, quant);
        var match = string.IsNullOrWhiteSpace(quant) ? null : files.FirstOrDefault(f => f.Quant.Equals(quant.Trim(), StringComparison.OrdinalIgnoreCase));
        return new RepoSize(files.Count, files.Min(f => f.SizeBytes), files.Max(f => f.SizeBytes), match?.SizeBytes, quant);
    }

    /// <summary>Sizes of several repositories (at most four requests at a time, file lists cached for an hour). A repository that cannot be read, such as a gated or unknown one, is simply missing from the result.</summary>
    public static async Task<Dictionary<string, RepoSize>> GetAsync(IReadOnlyDictionary<string, string?> repos, CancellationToken ct = default)
    {
        var result = new ConcurrentDictionary<string, RepoSize>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(repos, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (kv, token) =>
        {
            try
            {
                if (!Cache.TryGetValue(kv.Key, out var hit) || DateTime.UtcNow - hit.At > MaxAge)
                {
                    var files = Lister != null ? await Lister(kv.Key, token) : await HuggingFace.ListFilesAsync(kv.Key, token);
                    Cache[kv.Key] = hit = (DateTime.UtcNow, files);
                }
                result[kv.Key] = Summarize(hit.Files, kv.Value);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* gated, unknown or offline: no size */ }
        });
        return new Dictionary<string, RepoSize>(result, StringComparer.OrdinalIgnoreCase);
    }

    public static void ClearCache() => Cache.Clear();
}
