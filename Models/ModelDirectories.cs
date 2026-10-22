using LLMHostProject.Core;

namespace LLMHostProject.Models;

public record RootUsage(string Path, string Role, bool Exists, long FreeBytes, long TotalBytes, long ModelBytes, int ModelCount, long PartialBytes);
public record PartialFile(string Path, long Bytes, double AgeDays);
public record ModelSize(string Id, long Bytes, string Root);
public record DiskReport(IReadOnlyList<RootUsage> Roots, IReadOnlyList<ModelSize> Largest, IReadOnlyList<PartialFile> Partials, long TotalModelBytes);

/// <summary>Progress of moving or copying the models folder. State: running | completed | failed | cancelled.</summary>
public class MoveJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Mode { get; init; } = "move";
    public string State { get; set; } = "running";
    public long TotalBytes { get; set; }
    public long BytesDone;
    public int Files { get; set; }
    public int FilesDone;
    public string? Current { get; set; }
    public string? Error { get; set; }
    public double Progress => TotalBytes > 0 ? Math.Min(1.0, (double)Interlocked.Read(ref BytesDone) / TotalBytes) : State == "completed" ? 1 : 0;
    internal CancellationTokenSource Cts { get; } = new();
}

/// <summary>
/// Looks after the folders the models live in: usage per folder, stale partial downloads, adding extra library folders and moving or
/// copying the main folder (also across drives) while the app keeps running.
/// </summary>
public class ModelDirectoryManager
{
    private readonly ModelLibrary _library;
    private readonly DownloadManager _downloads;
    private readonly Func<IEnumerable<string>> _loadedPaths;
    private readonly object _gate = new();
    public MoveJob? Last { get; private set; }

    public ModelDirectoryManager(ModelLibrary library, DownloadManager downloads, Func<IEnumerable<string>> loadedModelPaths)
    {
        _library = library; _downloads = downloads; _loadedPaths = loadedModelPaths;
    }

    private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    private static bool Inside(string child, string parent)
    {
        string c = Norm(child), p = Norm(parent);
        return c.Equals(p, StringComparison.OrdinalIgnoreCase) || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ usage

    /// <summary>.part files that no unfinished download job is using: leftovers of cancelled or removed downloads.</summary>
    public IReadOnlyList<PartialFile> StalePartials()
    {
        var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in _downloads.Jobs.Where(j => j.State is not (DownloadState.Completed or DownloadState.Cancelled)))
            foreach (var p in j.Parts) inUse.Add(Path.GetFullPath(Path.Combine(j.DestinationDirectory, p.FileName)) + ".part");
        var list = new List<PartialFile>();
        foreach (var root in AppSettings.Current.ModelRoots.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories))
            {
                if (inUse.Contains(Path.GetFullPath(f))) continue;
                var fi = new FileInfo(f);
                list.Add(new PartialFile(f, fi.Length, Math.Round((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalDays, 1)));
            }
        return list;
    }

    public DiskReport Report(int largest = 10)
    {
        var roots = AppSettings.Current.ModelRoots;
        var models = _library.List();
        var partials = StalePartials();
        var usage = new List<RootUsage>();
        for (int i = 0; i < roots.Count; i++)
        {
            string r = roots[i];
            bool exists = Directory.Exists(r);
            long free = 0, total = 0;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(r));
                if (root != null) { var d = new DriveInfo(root); free = d.AvailableFreeSpace; total = d.TotalSize; }
            }
            catch { }
            var mine = models.Where(m => string.Equals(Path.TrimEndingDirectorySeparator(m.Root), Path.TrimEndingDirectorySeparator(r), StringComparison.OrdinalIgnoreCase)).ToList();
            usage.Add(new RootUsage(r, i == 0 ? "download" : "extra", exists, free, total, mine.Sum(m => m.SizeBytes), mine.Count,
                partials.Where(p => Inside(p.Path, r)).Sum(p => p.Bytes)));
        }
        return new DiskReport(usage, models.OrderByDescending(m => m.SizeBytes).Take(largest).Select(m => new ModelSize(m.Id, m.SizeBytes, m.Root)).ToList(),
            partials, models.Sum(m => m.SizeBytes));
    }

    /// <summary>Deletes the stale partial downloads. Returns the number of files and bytes removed.</summary>
    public (int Files, long Bytes) CleanPartials()
    {
        int n = 0; long bytes = 0;
        foreach (var p in StalePartials())
        {
            try { File.Delete(p.Path); n++; bytes += p.Bytes; } catch { }
        }
        return (n, bytes);
    }

    // ------------------------------------------------------------------ extra folders

    public void AddRoot(string path)
    {
        string full = Norm(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"The folder does not exist: {full}");
        var s = AppSettings.Current;
        if (s.ModelRoots.Any(r => Norm(r).Equals(full, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("That folder is already part of the library.");
        s.ExtraModelsDirectories.Add(full);
        s.Save();
    }

    public void RemoveRoot(string path)
    {
        string full = Norm(path);
        var s = AppSettings.Current;
        if (Norm(s.ResolvedModelsDirectory).Equals(full, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That is the main folder (where downloads go); move it instead, or choose another main folder first.");
        if (_loadedPaths().Any(p => Inside(p, full))) throw new InvalidOperationException("A model from that folder is loaded; unload it first.");
        int removed = s.ExtraModelsDirectories.RemoveAll(r => { try { return Norm(r).Equals(full, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
        if (removed == 0) throw new InvalidOperationException("That folder is not one of the extra library folders.");
        s.Save();
    }

    // ------------------------------------------------------------------ move / copy / switch

    /// <summary>
    /// Makes <paramref name="to"/> the main folder. <c>move</c> moves the files, <c>copy</c> copies them and leaves the originals,
    /// <c>switchOnly</c> just changes the setting (the files were moved by hand). Returns at once; progress is in <see cref="Last"/>.
    /// </summary>
    public MoveJob StartMove(string to, string mode)
    {
        mode = (mode ?? "move").Trim().ToLowerInvariant();
        if (mode is not ("move" or "copy" or "switchonly")) throw new ArgumentException("mode must be move, copy or switchOnly.");
        if (string.IsNullOrWhiteSpace(to)) throw new ArgumentException("A target folder is required.");
        string target = Norm(to);
        string from = Norm(AppSettings.Current.ResolvedModelsDirectory);
        if (target.Equals(from, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("That already is the main folder.");
        if (mode != "switchonly" && (Inside(target, from) || Inside(from, target)))
            throw new InvalidOperationException("The target folder must not be inside the current one, nor the other way round.");
        if (_downloads.Jobs.Any(j => j.IsActive)) throw new InvalidOperationException("Downloads are running; pause or finish them first.");
        if (mode == "move" && _loadedPaths().Any(p => Inside(p, from))) throw new InvalidOperationException("A model from this folder is loaded; unload it before moving the folder.");
        lock (_gate)
        {
            if (Last is { State: "running" }) throw new InvalidOperationException("A move is already running.");
            Directory.CreateDirectory(target);

            var plan = new List<(string Source, string Dest, long Size)>();
            if (mode != "switchonly" && Directory.Exists(from))
            {
                var conflicts = new List<string>();
                foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(from, f);
                    string dest = Path.Combine(target, rel);
                    long size = new FileInfo(f).Length;
                    if (File.Exists(dest) && new FileInfo(dest).Length != size) conflicts.Add(rel);
                    plan.Add((f, dest, size));
                }
                if (conflicts.Count > 0)
                    throw new IOException($"The target already holds different files with the same names: {string.Join(", ", conflicts.Take(5))}{(conflicts.Count > 5 ? "…" : "")}");
                long needed = plan.Where(p => !File.Exists(p.Dest)).Sum(p => p.Size);
                var root = Path.GetPathRoot(target);
                if (root != null && new DriveInfo(root).AvailableFreeSpace < needed + (256L << 20))
                    throw new IOException($"Not enough free space on {root}: {Hardware.FormatBytes(needed)} needed.");
            }

            var job = new MoveJob { From = from, To = target, Mode = mode == "switchonly" ? "switchOnly" : mode, TotalBytes = plan.Sum(p => p.Size), Files = plan.Count };
            Last = job;
            _ = Task.Run(() => RunMove(job, plan, mode));
            return job;
        }
    }

    public bool CancelMove()
    {
        var j = Last;
        if (j is not { State: "running" }) return false;
        j.Cts.Cancel();
        return true;
    }

    private void RunMove(MoveJob job, List<(string Source, string Dest, long Size)> plan, string mode)
    {
        try
        {
            string sameVolume = Path.GetPathRoot(job.From) ?? "";
            foreach (var (src, dest, size) in plan)
            {
                job.Cts.Token.ThrowIfCancellationRequested();
                job.Current = Path.GetRelativePath(job.From, src);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                bool already = File.Exists(dest) && new FileInfo(dest).Length == size;
                if (!already)
                {
                    if (mode == "move" && string.Equals(sameVolume, Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Move(src, dest);                                                 // same drive: a rename, nothing to copy
                        Interlocked.Add(ref job.BytesDone, size);
                        job.FilesDone++;
                        continue;
                    }
                    CopyWithProgress(src, dest, job);
                }
                else Interlocked.Add(ref job.BytesDone, size);
                if (mode == "move")
                {
                    if (new FileInfo(dest).Length != size) throw new IOException($"{job.Current} was not copied completely; the original was kept.");
                    File.Delete(src);
                }
                job.FilesDone++;
            }
            if (mode == "move") RemoveEmptyDirectories(job.From);
            var s = AppSettings.Current;
            // the old main folder stays readable as an extra folder when its files were kept
            if (mode == "copy" && !s.ExtraModelsDirectories.Any(r => Norm(r).Equals(job.From, StringComparison.OrdinalIgnoreCase))) s.ExtraModelsDirectories.Add(job.From);
            s.ExtraModelsDirectories.RemoveAll(r => Norm(r).Equals(job.To, StringComparison.OrdinalIgnoreCase));
            s.ModelsDirectory = job.To;
            s.Save();
            job.Current = null;
            job.State = "completed";
            LogBuffer.Add("download", $"Models folder is now {job.To} ({job.Mode}).");
        }
        catch (OperationCanceledException) { job.State = "cancelled"; job.Error = "Cancelled. Files already copied stay in the target; the main folder was not changed."; }
        catch (Exception ex) { job.State = "failed"; job.Error = ex.Message; LogBuffer.Add("download", "Moving the models folder failed: " + ex.Message); }
    }

    private static void CopyWithProgress(string src, string dest, MoveJob job)
    {
        string tmp = dest + ".moving";
        try
        {
            using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[4 << 20];
                int n;
                while ((n = i.Read(buf, 0, buf.Length)) > 0)
                {
                    job.Cts.Token.ThrowIfCancellationRequested();
                    o.Write(buf, 0, n);
                    Interlocked.Add(ref job.BytesDone, n);
                }
            }
            File.Move(tmp, dest, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
    }
}
