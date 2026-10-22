using LLMHostProject.Core;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

/// <summary>Moves and merges real folders under a temp directory; the shared settings are put back afterwards.</summary>
[Trait("Category", "Unit")]
[Collection("Settings")]
public sealed class ModelDirectoryTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "llmhost-dirs-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _oldMain;
    private readonly List<string> _oldExtras;
    private readonly ModelLibrary _lib = new();
    private readonly DownloadManager _dl;
    private readonly List<string> _loaded = new();
    private readonly ModelDirectoryManager _dirs;

    public ModelDirectoryTests()
    {
        _oldMain = AppSettings.Current.ModelsDirectory;
        _oldExtras = AppSettings.Current.ExtraModelsDirectories.ToList();
        Directory.CreateDirectory(_work);
        _dl = new DownloadManager(Path.Combine(_work, "state.json"));
        _dirs = new ModelDirectoryManager(_lib, _dl, () => _loaded);
    }

    public void Dispose()
    {
        AppSettings.Current.ModelsDirectory = _oldMain;
        AppSettings.Current.ExtraModelsDirectories = _oldExtras;
        AppSettings.Current.Save();
        try { Directory.Delete(_work, true); } catch { }
    }

    private string Main(string name) { var d = Path.Combine(_work, name); Directory.CreateDirectory(d); AppSettings.Current.ModelsDirectory = d; AppSettings.Current.ExtraModelsDirectories = new(); return d; }

    private static string Model(string root, string rel, int blocks = 2)
    {
        string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, GgufBuilder.Build(blocks: blocks));
        return p;
    }

    private static async Task<MoveJob> Wait(MoveJob j)
    {
        for (int i = 0; i < 200 && j.State == "running"; i++) await Task.Delay(50);
        return j;
    }

    [Fact]
    public void ExtraFoldersAreMergedAndTheFirstFolderWinsOnTheSameId()
    {
        string main = Main("lib-a"), extra = Path.Combine(_work, "lib-b");
        Model(main, "pub/one/one-Q4_0.gguf");
        Model(main, "pub/dup/dup-Q4_0.gguf", blocks: 2);
        Model(extra, "pub/two/two-Q4_0.gguf");
        Model(extra, "pub/dup/dup-Q4_0.gguf", blocks: 6);                       // same id as in the main folder

        _dirs.AddRoot(extra);
        var all = _lib.List();
        Assert.Contains(all, m => m.Id == "pub/one/one-Q4_0.gguf" && m.Root == main);
        Assert.Contains(all, m => m.Id == "pub/two/two-Q4_0.gguf" && m.Root == extra);
        Assert.Single(all, m => m.Id == "pub/dup/dup-Q4_0.gguf");
        Assert.Equal(main, all.Single(m => m.Id == "pub/dup/dup-Q4_0.gguf").Root);

        Assert.Throws<InvalidOperationException>(() => _dirs.AddRoot(extra));        // twice
        Assert.Throws<DirectoryNotFoundException>(() => _dirs.AddRoot(Path.Combine(_work, "nowhere")));
        Assert.Throws<InvalidOperationException>(() => _dirs.RemoveRoot(main));       // the download folder stays

        _dirs.RemoveRoot(extra);
        Assert.DoesNotContain(_lib.List(), m => m.Root == extra);
    }

    [Fact]
    public void AFolderWithALoadedModelCannotBeRemoved()
    {
        string main = Main("lib-l"), extra = Path.Combine(_work, "lib-l2");
        string file = Model(extra, "pub/x/x-Q4_0.gguf");
        _dirs.AddRoot(extra);
        _loaded.Add(file);
        Assert.Throws<InvalidOperationException>(() => _dirs.RemoveRoot(extra));
        _loaded.Clear();
        _dirs.RemoveRoot(extra);
    }

    [Fact]
    public async Task MovingTheFolderMovesEveryFileAndSwitchesTheSetting()
    {
        string a = Main("move-a"), b = Path.Combine(_work, "move-b");
        string m1 = Model(a, "pub/one/one-Q4_0.gguf");
        Model(a, "pub/two/two-Q4_0.gguf");
        File.WriteAllText(m1 + ".llmhost.json", "{}");                           // sidecars travel with the model
        File.WriteAllBytes(Path.Combine(a, "pub", "one", "leftover.gguf.part"), new byte[100]);

        var job = await Wait(_dirs.StartMove(b, "move"));
        Assert.Equal("completed", job.State);
        Assert.Equal(4, job.FilesDone);
        Assert.Equal(1.0, job.Progress);
        Assert.Equal(Path.GetFullPath(b), AppSettings.Current.ResolvedModelsDirectory);
        Assert.True(File.Exists(Path.Combine(b, "pub", "one", "one-Q4_0.gguf")));
        Assert.True(File.Exists(Path.Combine(b, "pub", "one", "one-Q4_0.gguf.llmhost.json")));
        Assert.False(File.Exists(m1));
        Assert.False(Directory.Exists(Path.Combine(a, "pub")));                  // emptied folders are removed
        Assert.Equal(2, _lib.List().Count(m => m.Root == Path.GetFullPath(b)));
    }

    [Fact]
    public async Task CopyingKeepsTheOriginalsAsAnExtraFolder()
    {
        string a = Main("copy-a"), b = Path.Combine(_work, "copy-b");
        Model(a, "pub/one/one-Q4_0.gguf");
        var job = await Wait(_dirs.StartMove(b, "copy"));
        Assert.Equal("completed", job.State);
        Assert.True(File.Exists(Path.Combine(a, "pub", "one", "one-Q4_0.gguf")));
        Assert.True(File.Exists(Path.Combine(b, "pub", "one", "one-Q4_0.gguf")));
        Assert.Equal(Path.GetFullPath(b), AppSettings.Current.ResolvedModelsDirectory);
        Assert.Contains(AppSettings.Current.ModelRoots, r => string.Equals(Path.TrimEndingDirectorySeparator(r), Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), StringComparison.OrdinalIgnoreCase));
        Assert.Single(_lib.List(), m => m.Id == "pub/one/one-Q4_0.gguf");        // one id, found once
    }

    [Fact]
    public async Task SwitchOnlyJustChangesTheSetting()
    {
        string a = Main("sw-a"), b = Path.Combine(_work, "sw-b");
        Model(a, "pub/one/one-Q4_0.gguf");
        Model(b, "pub/two/two-Q4_0.gguf");                                      // the user already moved things by hand
        var job = await Wait(_dirs.StartMove(b, "switchOnly"));
        Assert.Equal("completed", job.State);
        Assert.True(File.Exists(Path.Combine(a, "pub", "one", "one-Q4_0.gguf")));
        Assert.Equal(new[] { "pub/two/two-Q4_0.gguf" }, _lib.List().Select(m => m.Id));
    }

    [Fact]
    public void BadMovesAreRefusedBeforeAnythingHappens()
    {
        string a = Main("bad-a");
        string file = Model(a, "pub/one/one-Q4_0.gguf");
        Assert.Throws<InvalidOperationException>(() => _dirs.StartMove(a, "move"));                               // same folder
        Assert.Throws<InvalidOperationException>(() => _dirs.StartMove(Path.Combine(a, "inner"), "move"));        // inside itself
        Assert.Throws<ArgumentException>(() => _dirs.StartMove(Path.Combine(_work, "x"), "teleport"));
        Assert.Throws<ArgumentException>(() => _dirs.StartMove("", "move"));

        _loaded.Add(file);
        Assert.Throws<InvalidOperationException>(() => _dirs.StartMove(Path.Combine(_work, "bad-b"), "move"));    // a model is loaded from it
        _loaded.Clear();

        // the target holds a different file under the same name
        string b = Path.Combine(_work, "bad-c");
        Model(b, "pub/one/one-Q4_0.gguf", blocks: 9);
        var ex = Assert.Throws<IOException>(() => _dirs.StartMove(b, "move"));
        Assert.Contains("same names", ex.Message);
        Assert.True(File.Exists(file));
        Assert.Equal(Path.GetFullPath(a), AppSettings.Current.ResolvedModelsDirectory);
    }

    [Fact]
    public void TheReportCountsSizesAndFindsStalePartials()
    {
        string a = Main("rep-a");
        Model(a, "pub/small/small-Q4_0.gguf", blocks: 2);
        Model(a, "pub/big/big-Q4_0.gguf", blocks: 12);
        File.WriteAllBytes(Path.Combine(a, "pub", "big", "gone-Q4_0.gguf.part"), new byte[4096]);

        var r = _dirs.Report();
        Assert.Equal(Path.GetFullPath(a), r.Roots[0].Path);
        Assert.Equal("download", r.Roots[0].Role);
        Assert.Equal(2, r.Roots[0].ModelCount);
        Assert.Equal(r.TotalModelBytes, r.Roots[0].ModelBytes);
        Assert.True(r.Roots[0].FreeBytes > 0 && r.Roots[0].TotalBytes >= r.Roots[0].FreeBytes);
        Assert.Equal("pub/big/big-Q4_0.gguf", r.Largest[0].Id);                  // largest first
        Assert.Equal(4096, r.Roots[0].PartialBytes);
        Assert.Single(r.Partials);

        var (files, bytes) = _dirs.CleanPartials();
        Assert.Equal((1, 4096L), (files, bytes));
        Assert.Empty(_dirs.StalePartials());
        Assert.Equal(2, _lib.List().Count);                                       // models untouched
    }
}

/// <summary>
/// Tests that change the shared settings (main models folder, token) run alone: other tests read the same static settings while
/// they run in parallel.
/// </summary>
[CollectionDefinition("Settings", DisableParallelization = true)]
public class SettingsCollection { }

[CollectionDefinition("Catalog", DisableParallelization = true)]
public class CatalogCollection { }
