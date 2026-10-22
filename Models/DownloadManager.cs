using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LLMHostProject.Core;

namespace LLMHostProject.Models;

/// <summary>NeedsAccess: Hugging Face refused the file because the repository is gated (licence not accepted / no token).</summary>
public enum DownloadState { Queued, Downloading, Paused, Completed, Failed, Cancelled, NeedsAccess }

/// <summary>One file of a download (a model shard, or the multimodal projector).</summary>
public class DownloadPart
{
    /// <summary>Path of the file in the source repository (the file name is used on disk).</summary>
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Expected SHA-256 (hex) when the source publishes one (Hugging Face LFS files do).</summary>
    public string? Sha256 { get; set; }
    public string FileName => Path.GetFileName(Name);
    /// <summary>The digest was compared with the downloaded bytes in this run.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Checked { get; set; }
}

public class DownloadJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public long Sequence { get; init; }
    public string Repo { get; init; } = "";
    public string FileName { get; init; } = "";
    public List<DownloadPart> Parts { get; init; } = new();
    public string DestinationDirectory { get; init; } = "";
    public DownloadState State { get; set; } = DownloadState.Queued;
    public long BytesDone;
    public long TotalBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public string? Error { get; set; }
    /// <summary>Transient information: retrying, verifying, waiting for a slot...</summary>
    public string? Note { get; set; }
    public string? FinalPath { get; set; }
    public bool Verified { get; set; }
    public int Attempt { get; set; }
    /// <summary>An update: the file that is already on disk is kept until the new one is complete and verified, then swapped in.</summary>
    public bool Replace { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    internal CancellationTokenSource? Cts { get; set; }
    internal bool PauseRequested { get; set; }
    public double Progress => TotalBytes > 0 ? Math.Min(1.0, (double)Interlocked.Read(ref BytesDone) / TotalBytes) : 0;
    public bool IsActive => State is DownloadState.Queued or DownloadState.Downloading;
    public IEnumerable<string> Shards => Parts.Select(p => p.Name);
}

/// <summary>Persisted form of a job (see <see cref="DownloadManager"/>).</summary>
internal class DownloadRecord
{
    public string Id { get; set; } = "";
    public long Sequence { get; set; }
    public string Repo { get; set; } = "";
    public string FileName { get; set; } = "";
    public List<DownloadPart> Parts { get; set; } = new();
    public string DestinationDirectory { get; set; } = "";
    public string State { get; set; } = "";
    public long TotalBytes { get; set; }
    public string? Error { get; set; }
    public string? FinalPath { get; set; }
    public bool Verified { get; set; }
    public bool Replace { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Background downloads into the models directory: a queue with a concurrency limit, pause / resume,
/// automatic retry with back-off, disk-space check, SHA-256 verification, and persistence across restarts.
/// </summary>
public class DownloadManager
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly object _gate = new();
    private long _sequence;
    private readonly string _stateFile;
    private const int MaxAttempts = 6;
    private const long DiskReserve = 256L * 1024 * 1024;

    public DownloadManager(string? stateFile = null)
    {
        _stateFile = stateFile ?? AppPaths.DownloadsFile;
        Restore();
    }

    public IReadOnlyList<DownloadJob> Jobs => _jobs.Values.OrderBy(j => j.Sequence).ToList();
    public DownloadJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public event Action<DownloadJob>? Finished;

    /// <summary>How many downloads run at the same time (extra jobs wait in the queue).</summary>
    public int MaxConcurrent { get; set; } = 2;

    // ------------------------------------------------------------------ creating jobs

    public DownloadJob Start(RemoteFile file, bool includeMmproj = true, bool replace = false)
    {
        var existing = _jobs.Values.FirstOrDefault(j => j.Repo == file.Repo && j.FileName == file.FileName
                                                        && j.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused);
        if (existing != null) { if (existing.State == DownloadState.Paused) Resume(existing.Id); return existing; }

        var parts = new List<DownloadPart>();
        foreach (var shard in file.Shards)
            parts.Add(new DownloadPart
            {
                Name = shard, Url = HuggingFace.ResolveUrl(file.Repo, shard),
                Size = file.Sizes?.GetValueOrDefault(shard) ?? (file.Shards.Count == 1 ? file.SizeBytes : 0), Sha256 = file.Hashes?.GetValueOrDefault(shard),
            });
        if (includeMmproj && file.MmprojFile != null)
            parts.Add(new DownloadPart
            {
                Name = file.MmprojFile, Url = HuggingFace.ResolveUrl(file.Repo, file.MmprojFile),
                Size = file.MmprojSize, Sha256 = file.Hashes?.GetValueOrDefault(file.MmprojFile),
            });
        string[] repoParts = file.Repo.Split('/');
        string destDir = Path.Combine(AppSettings.Current.ResolvedModelsDirectory, repoParts[0], repoParts.Length > 1 ? repoParts[1] : "_");
        long total = includeMmproj && file.MmprojFile != null ? file.TotalDownloadBytes : file.SizeBytes;
        return Enqueue(file.Repo, file.FileName, parts, destDir, total, replace);
    }

    /// <summary>Downloads an arbitrary GGUF URL (not necessarily Hugging Face).</summary>
    public DownloadJob StartUrl(string url, string? publisher = null, string? sha256 = null, bool replace = false)
    {
        var uri = new Uri(url);
        string name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The URL must point to a .gguf file.");
        string pub = Sanitize(publisher ?? uri.Host);
        string destDir = Path.Combine(AppSettings.Current.ResolvedModelsDirectory, pub, Sanitize(Path.GetFileNameWithoutExtension(name)));
        var part = new DownloadPart { Name = name, Url = url, Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant() };
        return Enqueue(pub + "/" + Path.GetFileNameWithoutExtension(name), name, new List<DownloadPart> { part }, destDir, 0, replace);
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')).Trim('.');

    private DownloadJob Enqueue(string repo, string fileName, List<DownloadPart> parts, string destDir, long total, bool replace = false)
    {
        var job = new DownloadJob
        {
            Repo = repo, FileName = fileName, Parts = parts, DestinationDirectory = destDir, TotalBytes = total,
            Sequence = Interlocked.Increment(ref _sequence), Replace = replace,
        };
        _jobs[job.Id] = job;
        Persist();
        Pump();
        return job;
    }

    // ------------------------------------------------------------------ control

    public bool Pause(string id)
    {
        if (!_jobs.TryGetValue(id, out var j)) return false;
        lock (_gate)
        {
            if (j.State == DownloadState.Queued) { j.State = DownloadState.Paused; j.Note = null; }
            else if (j.State == DownloadState.Downloading) { j.PauseRequested = true; j.Cts?.Cancel(); }
            else return false;
        }
        Persist();
        return true;
    }

    public bool Resume(string id)
    {
        if (!_jobs.TryGetValue(id, out var j)) return false;
        lock (_gate)
        {
            if (j.State is not (DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled or DownloadState.NeedsAccess)) return false;
            j.State = DownloadState.Queued; j.Error = null; j.Note = null; j.Attempt = 0;
        }
        Persist();
        Pump();
        return true;
    }

    /// <summary>Stops a download. The partial file is kept so the same download can be resumed later.</summary>
    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var j)) return false;
        lock (_gate)
        {
            if (j.State == DownloadState.Downloading) { j.PauseRequested = false; j.Cts?.Cancel(); }
            else if (j.State is DownloadState.Queued or DownloadState.Paused) j.State = DownloadState.Cancelled;
            else return false;
        }
        Persist();
        return true;
    }

    /// <summary>Removes a job from the list; with <paramref name="deleteFiles"/> also its partial files.</summary>
    public bool Remove(string id, bool deleteFiles = false)
    {
        if (!_jobs.TryGetValue(id, out var j)) return false;
        if (j.State == DownloadState.Downloading) { j.PauseRequested = false; j.Cts?.Cancel(); }
        _jobs.TryRemove(id, out _);
        if (deleteFiles && j.State != DownloadState.Completed)
            foreach (var p in j.Parts)
                try { File.Delete(Path.Combine(j.DestinationDirectory, p.FileName) + ".part"); } catch { }
        Persist();
        return true;
    }

    /// <summary>Removes completed, failed and cancelled jobs from the list.</summary>
    public int ClearFinished()
    {
        int n = 0;
        foreach (var j in _jobs.Values)
            if (j.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled or DownloadState.NeedsAccess && _jobs.TryRemove(j.Id, out _)) n++;
        Persist();
        return n;
    }

    /// <summary>Re-queues the jobs that were running or queued when the app last exited (call from the server host).</summary>
    public int ResumeInterrupted()
    {
        int n = 0;
        foreach (var j in _jobs.Values.Where(j => j.State == DownloadState.Paused && j.Note == "interrupted by restart"))
            if (Resume(j.Id)) n++;
        return n;
    }

    // ------------------------------------------------------------------ scheduling

    private void Pump()
    {
        List<DownloadJob> toStart = new();
        lock (_gate)
        {
            int running = _jobs.Values.Count(j => j.State == DownloadState.Downloading);
            foreach (var j in _jobs.Values.Where(j => j.State == DownloadState.Queued).OrderBy(j => j.Sequence))
            {
                if (running >= Math.Max(1, MaxConcurrent)) { j.Note = "waiting in the queue"; continue; }
                j.State = DownloadState.Downloading;
                j.Note = null;
                j.PauseRequested = false;
                j.Cts = new CancellationTokenSource();
                running++;
                toStart.Add(j);
            }
        }
        foreach (var j in toStart) _ = Task.Run(() => RunAsync(j));
    }

    // ------------------------------------------------------------------ persistence

    private void Persist()
    {
        try
        {
            lock (_gate)
            {
                var records = _jobs.Values.OrderBy(j => j.Sequence).Select(j => new DownloadRecord
                {
                    Id = j.Id, Sequence = j.Sequence, Repo = j.Repo, FileName = j.FileName, Parts = j.Parts,
                    DestinationDirectory = j.DestinationDirectory, State = j.State.ToString(), TotalBytes = j.TotalBytes,
                    Error = j.Error, FinalPath = j.FinalPath, Verified = j.Verified, CreatedAt = j.CreatedAt, Replace = j.Replace,
                }).ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
                File.WriteAllText(_stateFile, JsonSerializer.Serialize(records, Json.Options));
            }
        }
        catch { /* persistence is best effort */ }
    }

    private void Restore()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var records = JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(_stateFile), Json.Options);
            if (records == null) return;
            foreach (var r in records)
            {
                if (!Enum.TryParse<DownloadState>(r.State, out var state)) state = DownloadState.Failed;
                bool wasActive = state is DownloadState.Queued or DownloadState.Downloading;
                var j = new DownloadJob
                {
                    Id = r.Id, Sequence = r.Sequence, Repo = r.Repo, FileName = r.FileName, Parts = r.Parts,
                    DestinationDirectory = r.DestinationDirectory, TotalBytes = r.TotalBytes, CreatedAt = r.CreatedAt, Replace = r.Replace,
                    // jobs that were running come back paused; the server resumes them (see ResumeInterrupted)
                    State = wasActive ? DownloadState.Paused : state,
                    Error = r.Error, FinalPath = r.FinalPath, Verified = r.Verified,
                    Note = wasActive ? "interrupted by restart" : null,
                };
                if (j.State != DownloadState.Completed) j.BytesDone = ExistingBytes(j);
                else j.BytesDone = j.TotalBytes;
                _jobs[j.Id] = j;
                _sequence = Math.Max(_sequence, j.Sequence);
            }
        }
        catch { /* ignore a corrupt state file */ }
    }

    private static long ExistingBytes(DownloadJob job)
    {
        long n = 0;
        foreach (var p in job.Parts)
        {
            string final = Path.Combine(job.DestinationDirectory, p.FileName);
            try
            {
                if (File.Exists(final) && !job.Replace) n += new FileInfo(final).Length;
                else if (File.Exists(final + ".part")) n += new FileInfo(final + ".part").Length;
            }
            catch { }
        }
        return n;
    }

    // ------------------------------------------------------------------ the transfer

    /// <summary>Writes the <c>.llmhost.json</c> next to a finished Hugging Face download (see <see cref="ModelSidecar"/>); the commit is looked up in the background.</summary>
    private static void RememberOrigin(DownloadJob job)
    {
        if (job.Parts.Count == 0 || !job.Parts[0].Url.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase) || !HuggingFace.ValidRepo(job.Repo)) return;
        string path = Path.Combine(job.DestinationDirectory, job.Parts[0].FileName);
        var sha = job.Parts.ToDictionary(p => p.Name, p => string.IsNullOrEmpty(p.Sha256) ? null : p.Sha256);
        var sidecar = new ModelSidecar(job.Repo, job.Parts[0].FileName, sha, null, DateTime.UtcNow);
        sidecar.Write(path);
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var info = await HuggingFace.GetRepoInfoAsync(job.Repo, cts.Token);
                if (info?.Sha != null) (sidecar with { Revision = info.Sha }).Write(path);
            }
            catch { /* the revision is a nicety; the hashes already decide */ }
        });
    }

    private static bool Retryable(Exception ex) => ex switch
    {
        HttpRequestException h => h.StatusCode is null or System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests
            || (int)h.StatusCode >= 500,
        IOException => true,
        TaskCanceledException => true,      // HttpClient timeout (a user cancel is handled before)
        _ => false,
    };

    private async Task RunAsync(DownloadJob job)
    {
        var ct = job.Cts!.Token;
        try
        {
            Directory.CreateDirectory(job.DestinationDirectory);
            Interlocked.Exchange(ref job.BytesDone, ExistingBytes(job));

            // ---- disk space: what is still missing must fit on the target drive
            long missing = 0;
            foreach (var p in job.Parts)
            {
                string final = Path.Combine(job.DestinationDirectory, p.FileName);
                if (File.Exists(final) && !job.Replace) continue;
                long have = File.Exists(final + ".part") ? new FileInfo(final + ".part").Length : 0;
                if (p.Size > 0) missing += Math.Max(0, p.Size - have);
            }
            if (missing > 0)
            {
                var root = Path.GetPathRoot(Path.GetFullPath(job.DestinationDirectory));
                if (root != null)
                {
                    long free = new DriveInfo(root).AvailableFreeSpace;
                    if (free < missing + DiskReserve)
                        throw new IOException($"Not enough free disk space on {root}: {Hardware.FormatBytes(missing)} needed, " +
                                              $"{Hardware.FormatBytes(free)} available.");
                }
            }

            foreach (var part in job.Parts)
            {
                ct.ThrowIfCancellationRequested();
                await DownloadPartWithRetryAsync(job, part, ct);
            }

            job.FinalPath = Path.Combine(job.DestinationDirectory, job.Parts[0].FileName);
            job.Note = null;
            job.State = DownloadState.Completed;
            job.Verified = job.Parts.All(p => !string.IsNullOrEmpty(p.Sha256) && p.Checked);
            RememberOrigin(job);
            LogBuffer.Add("download", $"Completed {job.Repo}/{job.FileName}" + (job.Verified ? " (SHA-256 verified)" : ""));
        }
        catch (OperationCanceledException)
        {
            job.Note = null;
            job.State = job.PauseRequested ? DownloadState.Paused : DownloadState.Cancelled;
        }
        catch (GatedModelException g)
        {
            job.State = DownloadState.NeedsAccess;
            job.Note = null;
            job.Error = g.Message;
            LogBuffer.Add("download", $"Needs access {job.Repo}: {g.Message}");
        }
        catch (Exception ex)
        {
            job.State = DownloadState.Failed;
            job.Note = null;
            job.Error = ex.Message;
            LogBuffer.Add("download", $"Failed {job.Repo}/{job.FileName}: {ex.Message}");
        }
        finally
        {
            job.BytesPerSecond = 0;
            Persist();
            try { Finished?.Invoke(job); } catch { }
            Pump();
        }
    }

    private async Task DownloadPartWithRetryAsync(DownloadJob job, DownloadPart part, CancellationToken ct)
    {
        string final = Path.Combine(job.DestinationDirectory, part.FileName);
        if (File.Exists(final) && !job.Replace) return;

        for (int attempt = 1; ; attempt++)
        {
            job.Attempt = attempt;
            try
            {
                await DownloadPartAsync(job, part, final, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (Retryable(ex) && attempt < MaxAttempts)
            {
                int delay = Math.Min(30, 2 << (attempt - 1));
                job.Note = $"connection problem ({ex.Message}); retrying in {delay}s (attempt {attempt + 1}/{MaxAttempts})";
                LogBuffer.Add("download", $"{job.FileName}: {ex.Message}; retry {attempt + 1}/{MaxAttempts} in {delay}s");
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                job.Note = null;
            }
        }
    }

    private async Task DownloadPartAsync(DownloadJob job, DownloadPart part, string final, CancellationToken ct)
    {
        string partFile = final + ".part";
        long have = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;
        bool verify = !string.IsNullOrEmpty(part.Sha256);

        using var req = HuggingFace.Request(part.Url, have);
        using var resp = await HuggingFace.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // the partial file already holds everything: just verify it
            await FinishPartAsync(job, part, partFile, final, ct);
            return;
        }
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new GatedModelException(job.Repo, HuggingFace.HasToken);
        resp.EnsureSuccessStatusCode();

        bool resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent && have > 0;
        if (!resumed && have > 0)
        {
            // the server ignored the range request: start over
            Interlocked.Add(ref job.BytesDone, -have);
            have = 0;
        }
        if (resp.Content.Headers.ContentLength is long len)
        {
            long total = (resumed ? have : 0) + len;
            if (part.Size == 0 || part.Size != total)
            {
                // learn / correct the size (direct URLs have none up front)
                long delta = total - part.Size;
                part.Size = total;
                if (job.Parts.All(p => p.Size > 0)) job.TotalBytes = job.Parts.Sum(p => p.Size);
                else if (job.Parts.Count == 1) job.TotalBytes = total;
                _ = delta;
            }
        }

        using var hasher = verify ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        if (verify && resumed)
        {
            // feed the bytes that are already on disk so the final digest covers the whole file
            job.Note = "checking the partial file";
            await using var existing = new FileStream(partFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var rb = new byte[1 << 20];
            int rn;
            while ((rn = await existing.ReadAsync(rb, ct)) > 0) hasher!.AppendData(rb, 0, rn);
            job.Note = null;
        }

        var sw = Stopwatch.StartNew();
        long sessionBytes = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(partFile, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                hasher?.AppendData(buf, 0, n);
                Interlocked.Add(ref job.BytesDone, n);
                sessionBytes += n;
                double secs = sw.Elapsed.TotalSeconds;
                if (secs > 0.5) job.BytesPerSecond = sessionBytes / secs;
            }
        }

        if (hasher != null)
        {
            string actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(part.Sha256!, StringComparison.OrdinalIgnoreCase))
            {
                long size = new FileInfo(partFile).Length;
                try { File.Delete(partFile); } catch { }
                Interlocked.Add(ref job.BytesDone, -size);
                throw new InvalidDataException(
                    $"SHA-256 mismatch for {part.FileName}: expected {part.Sha256}, got {actual}. The corrupt file was removed; retry the download.");
            }
        }
        part.Checked = hasher != null;
        File.Move(partFile, final, true);
    }

    /// <summary>Verifies (when a digest is known) and finalizes a part file that was already fully downloaded.</summary>
    private static async Task FinishPartAsync(DownloadJob job, DownloadPart part, string partFile, string final, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(part.Sha256))
        {
            job.Note = "verifying SHA-256";
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var fs = new FileStream(partFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var buf = new byte[1 << 20];
            int n;
            while ((n = await fs.ReadAsync(buf, ct)) > 0) h.AppendData(buf, 0, n);
            string actual = Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
            job.Note = null;
            if (!actual.Equals(part.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                try { File.Delete(partFile); } catch { }
                throw new InvalidDataException($"SHA-256 mismatch for {part.FileName}: expected {part.Sha256}, got {actual}.");
            }
        }
        part.Checked = !string.IsNullOrEmpty(part.Sha256);
        File.Move(partFile, final, true);
    }
}
