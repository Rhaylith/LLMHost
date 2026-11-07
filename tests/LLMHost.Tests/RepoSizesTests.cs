using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
[Collection("Settings")]
public class RepoSizesTests : IDisposable
{
    private const long Gb = 1L << 30;
    private static RemoteFile F(string repo, string quant, long size) => new(repo, $"m-{quant}.gguf", size, quant, new[] { $"m-{quant}.gguf" });
    public void Dispose() { RepoSizes.Lister = null; RepoSizes.ClearCache(); }

    [Fact]
    public void ASummaryHasTheRangeAndTheSizeOfTheWantedQuantization()
    {
        var files = new[] { F("a/b", "Q2_K", 2 * Gb), F("a/b", "Q4_K_M", 5 * Gb), F("a/b", "Q8_0", 9 * Gb) };
        var s = RepoSizes.Summarize(files, "q4_k_m");
        Assert.Equal(3, s.Files); Assert.Equal(2 * Gb, s.MinBytes); Assert.Equal(9 * Gb, s.MaxBytes); Assert.Equal(5 * Gb, s.QuantBytes);
        Assert.Null(RepoSizes.Summarize(files, "Q5_K_M").QuantBytes);                       // the repository does not have it
        Assert.Equal(0, RepoSizes.Summarize(Array.Empty<RemoteFile>()).Files);
    }

    [Fact]
    public async Task EachRepositoryIsReadOnceAndOneThatFailsIsLeftOut()
    {
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        RepoSizes.Lister = (repo, _) =>
        {
            calls.AddOrUpdate(repo, 1, (_, n) => n + 1);
            if (repo == "gated/one") throw new HttpRequestException("403");
            return Task.FromResult<IReadOnlyList<RemoteFile>>(new[] { F(repo, "Q4_K_M", 4 * Gb) });
        };
        var want = new Dictionary<string, string?> { ["ok/one"] = "Q4_K_M", ["gated/one"] = null };
        var r = await RepoSizes.GetAsync(want);
        Assert.Equal(new[] { "ok/one" }, r.Keys);
        Assert.Equal(4 * Gb, r["ok/one"].QuantBytes);
        await RepoSizes.GetAsync(want);
        Assert.Equal(1, calls["ok/one"]);                                                    // cached
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class RepoSizesApiTests : IClassFixture<ServerFixture>, IDisposable
{
    private readonly ServerFixture _f;
    public RepoSizesApiTests(ServerFixture f) => _f = f;
    public void Dispose() { RepoSizes.Lister = null; RepoSizes.ClearCache(); }

    [Fact]
    public async Task TheEndpointAnswersWithSizesAndIgnoresBadRepositoryNames()
    {
        var asked = new List<string>();
        RepoSizes.Lister = (repo, _) => { lock (asked) asked.Add(repo); return Task.FromResult<IReadOnlyList<RemoteFile>>(new[] { new RemoteFile(repo, "x.gguf", 3000, "Q4_K_M", new[] { "x.gguf" }), new RemoteFile(repo, "y.gguf", 9000, "Q8_0", new[] { "y.gguf" }) }); };
        var body = """{"repos":["good/repo",{"repo":"other/repo","quant":"Q8_0"},"../etc/passwd","no-slash","a/b/c"]}""";
        var resp = await _f.Http.PostAsync("/api/models/repo-sizes", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["sizes"]!.AsObject();
        Assert.Equal(2, j.Count);
        Assert.Equal(3000, (long)j["good/repo"]!["minBytes"]!);
        Assert.Equal(9000, (long)j["other/repo"]!["quantBytes"]!);
        Assert.Equal(2, (int)j["good/repo"]!["files"]!);
        Assert.DoesNotContain(asked, r => r.Contains("passwd") || r == "no-slash");          // nothing invalid reaches Hugging Face
    }
}
