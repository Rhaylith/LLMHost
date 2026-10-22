using LLMHostProject.Core;

namespace LLMHostProject.Models;

public class LocalModel
{
    public string Id { get; set; } = "";          // publisher/repo/file.gguf relative path (forward slashes)
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Architecture { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Quantization { get; set; }
    public string? SizeLabel { get; set; }
    public long ContextLength { get; set; }
    public bool IsEmbedding { get; set; }
    public bool HasChatTemplate { get; set; }
    public bool Split { get; set; }
    /// <summary>Multimodal projector found next to the model (null for text-only models).</summary>
    public string? MmprojPath { get; set; }
    public bool SupportsVision => MmprojPath != null;
    /// <summary>A LoRA adapter, not a model: it is listed separately and applied on top of a model of the same architecture.</summary>
    public bool IsAdapter { get; set; }
    /// <summary>The library folder the file was found in.</summary>
    public string Root { get; set; } = "";
    /// <summary>Vision / audio / tools / reasoning / embedding / MoE / rerank, derived from the GGUF headers.</summary>
    public ModelCapabilities Capabilities { get; set; } = ModelCapabilities.None;
}

/// <summary>Scans the models directory (publisher/repo/file.gguf layout, same as BlahLLMWrapper).</summary>
public class ModelLibrary
{
    private readonly Dictionary<string, (DateTime Stamp, GgufInfo? Info)> _cache = new();

    public string Directory => AppSettings.Current.ResolvedModelsDirectory;

    /// <summary>The models in the library (LoRA adapters are listed by <see cref="ListAdapters"/>).</summary>
    public IReadOnlyList<LocalModel> List() => Scan().Where(m => !m.IsAdapter).ToList();

    /// <summary>LoRA adapter files in the library.</summary>
    public IReadOnlyList<LocalModel> ListAdapters() => Scan().Where(m => m.IsAdapter).ToList();

    /// <summary>Finds an adapter by library id, file name or file path.</summary>
    public LocalModel? FindAdapter(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var all = ListAdapters();
        if (File.Exists(query))
        {
            var full = System.IO.Path.GetFullPath(query);
            return all.FirstOrDefault(m => string.Equals(m.Path, full, StringComparison.OrdinalIgnoreCase)) ?? AdapterFromFile(full);
        }
        query = query.Replace('\\', '/');
        return all.FirstOrDefault(m => m.Id.Equals(query, StringComparison.OrdinalIgnoreCase) || m.FileName.Equals(query, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(m => m.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static LocalModel? AdapterFromFile(string full)
    {
        var info = GgufInfo.TryRead(full);
        if (info == null) return null;
        return new LocalModel { Id = System.IO.Path.GetFileName(full), Path = full, FileName = System.IO.Path.GetFileName(full), SizeBytes = new FileInfo(full).Length, Architecture = info.Architecture, Name = info.Name, IsAdapter = info.IsAdapter };
    }

    private IReadOnlyList<LocalModel> Scan()
    {
        var result = new List<LocalModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);                  // the first folder wins for an id that exists twice
        foreach (var root in AppSettings.Current.ModelRoots) ScanRoot(root, seen, result);
        return result.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ScanRoot(string dir, HashSet<string> seen, List<LocalModel> result)
    {
        if (!System.IO.Directory.Exists(dir)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.gguf", SearchOption.AllDirectories))
        {
            string name = System.IO.Path.GetFileName(file);
            if (name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
            bool shard = System.Text.RegularExpressions.Regex.IsMatch(name, @"-\d{5}-of-\d{5}\.gguf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (shard && !name.Contains("-00001-of-", StringComparison.OrdinalIgnoreCase)) continue;

            var fi = new FileInfo(file);
            if (!_cache.TryGetValue(file, out var c) || c.Stamp != fi.LastWriteTimeUtc)
            {
                c = (fi.LastWriteTimeUtc, GgufInfo.TryRead(file));
                _cache[file] = c;
            }
            long size = fi.Length;
            if (shard)
            {
                var prefix = name[..name.IndexOf("-00001-of-", StringComparison.OrdinalIgnoreCase)];
                size = fi.Directory!.EnumerateFiles(prefix + "-*-of-*.gguf").Sum(f => f.Length);
            }
            var rel = System.IO.Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (!seen.Add(rel)) continue;
            var info = c.Info;
            string? mmproj = FindMmproj(file);
            result.Add(new LocalModel
            {
                Id = rel, Root = dir,
                Path = file,
                FileName = name,
                SizeBytes = size,
                Architecture = info?.Architecture ?? "",
                Name = string.IsNullOrEmpty(info?.Name) ? System.IO.Path.GetFileNameWithoutExtension(name) : info!.Name,
                Quantization = info?.Quantization ?? HuggingFace.ParseQuant(name),
                SizeLabel = info?.SizeLabel,
                ContextLength = info?.ContextLength ?? 0,
                IsEmbedding = info?.IsEmbeddingModel ?? false,
                HasChatTemplate = info?.HasChatTemplate ?? false,
                IsAdapter = info?.IsAdapter ?? false,
                Split = shard,
                MmprojPath = mmproj,
                Capabilities = ModelCapabilities.From(info, MmprojInfo(mmproj), name),
            });
        }
    }

    /// <summary>Header of a projector file (cached like the models). An unreadable projector still counts as vision.</summary>
    private GgufInfo? MmprojInfo(string? path)
    {
        if (path == null) return null;
        var stamp = new FileInfo(path).LastWriteTimeUtc;
        if (!_cache.TryGetValue(path, out var c) || c.Stamp != stamp)
        {
            c = (stamp, GgufInfo.TryRead(path));
            _cache[path] = c;
        }
        return c.Info ?? new GgufInfo { HasVisionEncoder = true };
    }

    /// <summary>
    /// Finds the multimodal projector (mmproj*.gguf) that belongs to a model file: same folder, preferring one
    /// that carries the model's name, then F16 over Q8_0.
    /// </summary>
    public static string? FindMmproj(string modelPath)
    {
        var dir = System.IO.Path.GetDirectoryName(modelPath);
        if (dir == null || !System.IO.Directory.Exists(dir)) return null;
        var candidates = System.IO.Directory.EnumerateFiles(dir, "mmproj*.gguf").ToList();
        if (candidates.Count == 0) return null;

        string modelName = System.IO.Path.GetFileNameWithoutExtension(modelPath);
        // drop quantisation / shard suffixes: "SmolVLM-256M-Instruct-Q8_0" -> "SmolVLM-256M-Instruct"
        string baseName = System.Text.RegularExpressions.Regex.Replace(modelName,
            @"[-_.](IQ\d_\w+|Q\d_\w+|BF16|F16|FP16|F32|MXFP4|\d{5}-of-\d{5})$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        int Score(string c)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(c).ToLowerInvariant();
            int score = n.Contains(baseName.ToLowerInvariant()) ? 100 : 0;
            if (n.Contains("f16") && !n.Contains("bf16")) score += 30;
            else if (n.Contains("bf16")) score += 20;
            else if (n.Contains("q8_0")) score += 10;
            return score;
        }
        return candidates.OrderByDescending(Score).First();
    }

    /// <summary>Find a model by id, relative path, file name, or unambiguous substring.</summary>
    public LocalModel? Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (File.Exists(query))
        {
            var full = System.IO.Path.GetFullPath(query);
            var known = List().FirstOrDefault(m => string.Equals(m.Path, full, StringComparison.OrdinalIgnoreCase));
            if (known != null) return known;
            var info = GgufInfo.TryRead(full);
            return new LocalModel
            {
                Id = System.IO.Path.GetFileName(full), Path = full, FileName = System.IO.Path.GetFileName(full),
                SizeBytes = new FileInfo(full).Length, Architecture = info?.Architecture ?? "",
                Name = info?.Name ?? "", Quantization = info?.Quantization, ContextLength = info?.ContextLength ?? 0,
                IsEmbedding = info?.IsEmbeddingModel ?? false,
                MmprojPath = FindMmproj(full),
                HasChatTemplate = info?.HasChatTemplate ?? false,
                Capabilities = ModelCapabilities.From(info, MmprojInfo(FindMmproj(full)), System.IO.Path.GetFileName(full)),
            };
        }
        var all = List();
        query = query.Replace('\\', '/');
        var m1 = all.FirstOrDefault(m => m.Id.Equals(query, StringComparison.OrdinalIgnoreCase)
                                         || m.FileName.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (m1 != null) return m1;
        var partial = all.Where(m => m.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : partial.OrderBy(m => m.SizeBytes).FirstOrDefault();
    }

    // ------------------------------------------------------------------ import

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr reserved);

    private static string SafeName(string s)
    {
        var cleaned = string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')).Trim('-', '.');
        return cleaned.Length == 0 ? "model" : cleaned;
    }

    /// <summary>
    /// Brings an existing .gguf file (all shards, and the multimodal projector next to it) into the library as
    /// <c>publisher/model/file.gguf</c>. <paramref name="mode"/> is "copy", "move" or "link" (symbolic link, falling back
    /// to a hard link on the same volume).
    /// </summary>
    public (string Id, string Path, IReadOnlyList<string> Files) Import(string source, string mode = "copy",
        string? publisher = null, string? name = null, bool includeMmproj = true)
    {
        source = System.IO.Path.GetFullPath(source.Trim().Trim('"'));
        if (!File.Exists(source)) throw new FileNotFoundException($"File not found: {source}");
        if (!source.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only .gguf files can be imported.");
        var info = GgufInfo.TryRead(source) ?? throw new InvalidDataException("That file is not a valid GGUF model (bad or truncated header).");

        string fileName = System.IO.Path.GetFileName(source);
        string pub = SafeName(string.IsNullOrWhiteSpace(publisher) ? "local" : publisher);
        string baseName = !string.IsNullOrWhiteSpace(name) ? name
            : !string.IsNullOrWhiteSpace(info.Name) ? info.Name
            : System.IO.Path.GetFileNameWithoutExtension(fileName);
        string destDir = System.IO.Path.Combine(Directory, pub, SafeName(baseName));
        System.IO.Directory.CreateDirectory(destDir);

        // everything that belongs to the model
        var files = new List<string> { source };
        var shard = System.Text.RegularExpressions.Regex.Match(fileName, @"^(.*)-(\d{5})-of-(\d{5})\.gguf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (shard.Success)
        {
            files = System.IO.Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(source)!, shard.Groups[1].Value + "-*-of-" + shard.Groups[3].Value + ".gguf")
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
        }
        if (includeMmproj && FindMmproj(source) is { } mm && !files.Contains(mm)) files.Add(mm);

        var placed = new List<string>();
        foreach (var f in files)
        {
            string dest = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(f));
            if (string.Equals(System.IO.Path.GetFullPath(f), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That file is already in the library.");
            if (File.Exists(dest)) throw new IOException($"{dest} already exists.");
            switch (mode.ToLowerInvariant())
            {
                case "move": File.Move(f, dest); break;
                case "link":
                    try { File.CreateSymbolicLink(dest, f); }
                    catch (Exception)
                    {
                        if (!CreateHardLink(dest, f, IntPtr.Zero))
                            throw new IOException("Could not create a link (symbolic links need developer mode or admin rights; " +
                                                  "hard links need the same drive). Use copy or move instead.");
                    }
                    break;
                default: File.Copy(f, dest); break;
            }
            placed.Add(dest);
        }
        string main = placed.First(p => System.IO.Path.GetFileName(p) == fileName || System.IO.Path.GetFileName(p).Contains("-00001-of-", StringComparison.OrdinalIgnoreCase));
        string id = System.IO.Path.GetRelativePath(Directory, main).Replace('\\', '/');
        return (id, main, placed);
    }

    public bool Delete(LocalModel m)
    {
        try
        {
            if (m.Split)
            {
                var prefix = m.FileName[..m.FileName.IndexOf("-00001-of-", StringComparison.OrdinalIgnoreCase)];
                foreach (var f in new DirectoryInfo(System.IO.Path.GetDirectoryName(m.Path)!).EnumerateFiles(prefix + "-*-of-*.gguf"))
                    f.Delete();
            }
            else File.Delete(m.Path);
            try { File.Delete(ModelSidecar.PathFor(m.Path)); } catch { }
            _cache.Remove(m.Path);
            return true;
        }
        catch { return false; }
    }
}
