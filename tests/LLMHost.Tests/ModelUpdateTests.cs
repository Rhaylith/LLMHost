using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class ModelUpdateCompareTests
{
    private static RemoteFile Remote(string name, string? sha) =>
        new("o/r", name, 1000, "Q4_K_M", new[] { name }, Hashes: sha == null ? null : new Dictionary<string, string> { [name] = sha });

    private static ModelSidecar Local(string name, string? sha) =>
        new("o/r", name, new Dictionary<string, string?> { [name] = sha }, "abc123", DateTime.UtcNow);

    [Fact]
    public void ASameChecksumIsCurrentAndADifferentOneIsAnUpdate()
    {
        Assert.Equal("current", ModelUpdateChecker.Compare(Local("m.gguf", "aa11"), new[] { Remote("m.gguf", "AA11") }).Status);
        var (status, local, remote, _) = ModelUpdateChecker.Compare(Local("m.gguf", "aa11"), new[] { Remote("m.gguf", "bb22") });
        Assert.Equal("update", status);
        Assert.Equal("aa11", local);
        Assert.Equal("bb22", remote);
    }

    [Fact]
    public void AFileThatLeftTheRepositoryIsReported() =>
        Assert.Equal("missing", ModelUpdateChecker.Compare(Local("old.gguf", "aa"), new[] { Remote("other.gguf", "aa") }).Status);

    [Fact]
    public void WithoutChecksumsNothingIsClaimed()
    {
        Assert.Equal("unknown", ModelUpdateChecker.Compare(Local("m.gguf", null), new[] { Remote("m.gguf", "aa") }).Status);
        Assert.Equal("unknown", ModelUpdateChecker.Compare(Local("m.gguf", "aa"), new[] { Remote("m.gguf", null) }).Status);
    }

    [Fact]
    public void ShardedModelsCompareEveryShard()
    {
        var a = "m-00001-of-00002.gguf"; var b = "m-00002-of-00002.gguf";
        var sc = new ModelSidecar("o/r", a, new Dictionary<string, string?> { [a] = "11", [b] = "22" }, null, DateTime.UtcNow);
        var same = new RemoteFile("o/r", a, 2000, "Q4_K_M", new[] { a, b }, Hashes: new Dictionary<string, string> { [a] = "11", [b] = "22" });
        var changed = new RemoteFile("o/r", a, 2000, "Q4_K_M", new[] { a, b }, Hashes: new Dictionary<string, string> { [a] = "11", [b] = "99" });
        Assert.Equal("current", ModelUpdateChecker.Compare(sc, new[] { same }).Status);
        Assert.Equal("update", ModelUpdateChecker.Compare(sc, new[] { changed }).Status);
    }

    [Fact]
    public void TheSidecarRoundTripsNextToTheModel()
    {
        string model = GgufBuilder.WriteFile("upd/side.gguf", GgufBuilder.Build());
        Assert.Null(ModelSidecar.Read(model));
        Local("side.gguf", "ff00").Write(model);
        Assert.True(File.Exists(model + ".llmhost.json"));
        var back = ModelSidecar.Read(model)!;
        Assert.Equal("o/r", back.Repo);
        Assert.Equal("ff00", back.Sha256["side.gguf"]);
        Assert.Equal("abc123", back.Revision);
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class ModelUpdateApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ModelUpdateApiTests(ServerFixture f) => _f = f;

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task AModelWithoutASidecarIsUnknownAndNeedsNoNetwork()
    {
        GgufBuilder.WriteFile("library/updpub/updmodel/upd-Q4_0.gguf", GgufBuilder.Build());
        var id = _f.Host.Library.List().First(m => m.FileName == "upd-Q4_0.gguf").Id;
        var r = await _f.Http.PostAsync("/api/models/updates/check", Body($"{{\"id\":\"{id}\"}}"));
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        Assert.Equal(1, (int)j["checkedCount"]!);
        Assert.Equal("unknown", (string)j["results"]![0]!["status"]!);
        Assert.Empty(j["updatesAvailable"]!.AsArray());

        var last = JsonNode.Parse(await _f.Http.GetStringAsync("/api/models/updates"))!;
        Assert.Contains(last["results"]!.AsArray(), x => (string)x!["modelId"]! == id);
    }

    [Fact]
    public async Task UpdatingAModelWithoutAKnownOriginIsRefused()
    {
        GgufBuilder.WriteFile("library/updpub/updmodel2/upd2-Q4_0.gguf", GgufBuilder.Build());
        var id = _f.Host.Library.List().First(m => m.FileName == "upd2-Q4_0.gguf").Id;
        var r = await _f.Http.PostAsync("/api/models/updates/apply", Body($"{{\"id\":\"{id}\"}}"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("origin", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeletingAModelRemovesItsSidecar()
    {
        string path = GgufBuilder.WriteFile("library/updpub/updmodel3/upd3-Q4_0.gguf", GgufBuilder.Build());
        new ModelSidecar("o/r", "upd3-Q4_0.gguf", new(), null, DateTime.UtcNow).Write(path);
        var id = _f.Host.Library.List().First(m => m.FileName == "upd3-Q4_0.gguf").Id;
        var r = await _f.Http.DeleteAsync("/api/models/local?id=" + Uri.EscapeDataString(id));
        Assert.True(r.IsSuccessStatusCode);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".llmhost.json"));
    }
}

[Trait("Category", "Unit")]
[Collection("Settings")]
public class ReplaceDownloadTests : IDisposable
{
    private readonly HttpListener _l = new();
    private readonly string _url;
    private readonly byte[] _new = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

    public ReplaceDownloadTests()
    {
        int port;
        using (var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { t.Start(); port = ((IPEndPoint)t.LocalEndpoint).Port; }
        _url = $"http://127.0.0.1:{port}/m/replace-me.gguf";
        _l.Prefixes.Add($"http://127.0.0.1:{port}/");
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                c.Response.ContentLength64 = _new.Length;
                await c.Response.OutputStream.WriteAsync(_new);
                c.Response.Close();
            }
        });
    }

    public void Dispose() { try { _l.Stop(); } catch { } }

    private static async Task Finish(DownloadJob j)
    {
        for (int i = 0; i < 100 && j.State is DownloadState.Queued or DownloadState.Downloading; i++) await Task.Delay(100);
    }

    [Fact]
    public async Task AnUpdateSwapsTheFileOnlyWhenTheNewOneIsComplete_AnOrdinaryDownloadKeepsTheOldOne()
    {
        var m = new DownloadManager(Path.Combine(TestEnvironment.Home, "dl-replace.json"));
        var first = m.StartUrl(_url, "replpub");
        await Finish(first);
        Assert.Equal(DownloadState.Completed, first.State);
        File.WriteAllBytes(first.FinalPath!, new byte[] { 1, 2, 3 });             // pretend this is the old version

        var plain = m.StartUrl(_url, "replpub");
        await Finish(plain);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first.FinalPath!));   // already there: nothing is fetched

        var update = m.StartUrl(_url, "replpub", replace: true);
        await Finish(update);
        Assert.Equal(DownloadState.Completed, update.State);
        Assert.Equal(_new, File.ReadAllBytes(first.FinalPath!));
        Assert.False(File.Exists(first.FinalPath + ".part"));
    }
}
