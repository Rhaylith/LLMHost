using System.Net;
using System.Text;
using LLMHostProject.Core;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class DiscoverSearchTests
{
    [Fact]
    public void TheSearchUrlCarriesSortFiltersAndCursor()
    {
        var url = HuggingFace.SearchUrl(new HuggingFace.HfSearch("qwen", "trending", "qwen2", null, 20, "abc%3D_1", Vision: true));
        Assert.Contains("filter=gguf", url);
        Assert.Contains("search=qwen", url);
        Assert.Contains("filter=qwen2", url);
        Assert.Contains("pipeline_tag=image-text-to-text", url);
        Assert.Contains("sort=trendingScore", url);
        Assert.Contains("direction=-1", url);
        Assert.Contains("limit=20", url);
        Assert.EndsWith("&cursor=abc%3D_1", url);
    }

    [Theory]
    [InlineData("downloads", "sort=downloads")]
    [InlineData("likes", "sort=likes")]
    [InlineData("updated", "sort=lastModified")]
    [InlineData("nonsense", "sort=downloads")]
    public void SortNamesAreMapped(string sort, string expected) => Assert.Contains(expected, HuggingFace.SearchUrl(new HuggingFace.HfSearch("x", sort)));

    [Fact]
    public void UnsafeValuesAreNotPassedOn()
    {
        var url = HuggingFace.SearchUrl(new HuggingFace.HfSearch("a b&c", "downloads", "x&evil=1", "bad pipeline", 9999, "x&y=1"));
        Assert.Contains("search=a%20b%26c", url);
        Assert.DoesNotContain("evil", url);
        Assert.DoesNotContain("pipeline_tag", url);
        Assert.DoesNotContain("y=1", url);
        Assert.Contains("limit=100", url);
    }

    [Fact]
    public void TheNextPageCursorIsTakenFromTheLinkHeader()
    {
        const string link = "<https://huggingface.co/api/models?filter=gguf&limit=30&cursor=eyJwIjoxfQ%3D%3D>; rel=\"next\"";
        Assert.Equal("eyJwIjoxfQ%3D%3D", HuggingFace.NextCursor(link));
        Assert.Null(HuggingFace.NextCursor(null));
        Assert.Null(HuggingFace.NextCursor("<https://x/y?cursor=1>; rel=\"prev\""));
        Assert.Null(HuggingFace.NextCursor("<https://x/y>; rel=\"next\""));
    }
}

[Trait("Category", "Unit")]
[Collection("Catalog")]
public class CatalogTests : IDisposable
{
    public CatalogTests() => Catalog.UseBuiltIn();
    public void Dispose() => Catalog.UseBuiltIn();

    private const string Good = """
        {"updated":"2030-01-02","entries":[
          {"key":"a","family":"F","name":"A","params":"1B","repo":"o/a-GGUF","recommendedQuant":"Q4_K_M","tags":["chat"]},
          {"key":"b","family":"F","name":"B","repo":"o/b-GGUF","tags":["vision"],"notes":"n"}]}
        """;

    [Fact]
    public void TheBuiltInListIsValidAndUnique()
    {
        Assert.Equal("built-in", Catalog.Source);
        Assert.True(Catalog.Entries.Count >= 30);
        Assert.False(string.IsNullOrEmpty(Catalog.Updated));
        Assert.Equal(Catalog.Entries.Count, Catalog.Entries.Select(e => e.Key).Distinct().Count());
        Assert.All(Catalog.Entries, e => Assert.True(HuggingFace.ValidRepo(e.Repo), e.Repo));
        Assert.NotNull(Catalog.Find("qwen3-14b"));
        Assert.True(Catalog.Find("smolvlm-256m")!.IsVision);
    }

    [Fact]
    public void AValidListIsParsed()
    {
        var (entries, updated) = Catalog.Parse(Good);
        Assert.Equal("2030-01-02", updated);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[1].IsVision);
        Assert.Equal("", entries[1].Params);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"entries\":[]}")]
    [InlineData("{\"entries\":[{\"key\":\"a\",\"family\":\"F\",\"name\":\"A\"}]}")]                                                       // no repo
    [InlineData("{\"entries\":[{\"key\":\"a\",\"family\":\"F\",\"name\":\"A\",\"repo\":\"../etc\"}]}")]                                  // not a repository
    [InlineData("{\"entries\":[{\"key\":\"a\",\"family\":\"F\",\"name\":\"A\",\"repo\":\"o/a\"},{\"key\":\"A\",\"family\":\"F\",\"name\":\"B\",\"repo\":\"o/b\"}]}")]   // duplicate key
    public void ABadListIsRefused(string json) => Assert.Throws<FormatException>(() => Catalog.Parse(json));

    private static HttpListener Serve(Func<HttpListenerRequest, (int Status, string Body, string? Etag)> handler, out string url)
    {
        var l = new HttpListener();
        int port;
        using (var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { t.Start(); port = ((IPEndPoint)t.LocalEndpoint).Port; }
        url = $"http://127.0.0.1:{port}/catalog.json";
        l.Prefixes.Add($"http://127.0.0.1:{port}/");
        l.Start();
        _ = Task.Run(async () =>
        {
            while (l.IsListening)
            {
                HttpListenerContext c;
                try { c = await l.GetContextAsync(); } catch { return; }
                var (status, body, etag) = handler(c.Request);
                c.Response.StatusCode = status;
                if (etag != null) c.Response.Headers["ETag"] = etag;
                var bytes = Encoding.UTF8.GetBytes(body);
                if (status != 304) await c.Response.OutputStream.WriteAsync(bytes);
                c.Response.Close();
            }
        });
        return l;
    }

    [Fact]
    public async Task ARemoteListReplacesTheBuiltInOneAndIsRevalidatedWithItsEtag()
    {
        int calls = 0;
        using var server = Serve(req =>
        {
            calls++;
            return req.Headers["If-None-Match"] == "\"v1\"" ? (304, "", null) : (200, Good, "\"v1\"");
        }, out var url);

        Assert.True(await Catalog.RefreshAsync(url));
        Assert.Equal("remote (cached)", Catalog.Source);
        Assert.Equal("2030-01-02", Catalog.Updated);
        Assert.Equal(2, Catalog.Entries.Count);

        Assert.False(await Catalog.RefreshAsync(url));                      // 304: unchanged
        Assert.Equal(2, calls);

        Catalog.Reset();                                                     // a restart picks the cached copy up again
        Assert.Equal(2, Catalog.Entries.Count);
        Catalog.UseBuiltIn();
        Assert.True(Catalog.Entries.Count >= 30);
    }

    [Fact]
    public async Task ABadRemoteListNeverReplacesAWorkingOne()
    {
        using var server = Serve(_ => (200, "{\"entries\":[{\"key\":\"x\"}]}", "\"bad\""), out var url);
        int before = Catalog.Entries.Count;
        await Assert.ThrowsAsync<FormatException>(() => Catalog.RefreshAsync(url));
        Assert.Equal(before, Catalog.Entries.Count);
        Assert.Equal("built-in", Catalog.Source);
    }

    [Fact]
    public async Task OnlyHttpAddressesAreAccepted() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Catalog.RefreshAsync("file:///c:/x.json"));

    [Fact]
    public void TheValidatorChecksTheCatalogAddress()
    {
        var s = new AppSettings { CatalogUrl = "ftp://x/y" };
        Assert.Contains("catalogUrl", SettingsValidator.Validate(s).Select(e => e.Field));
        s.CatalogUrl = "https://example.com/c.json";
        Assert.DoesNotContain("catalogUrl", SettingsValidator.Validate(s).Select(e => e.Field));
    }
}
