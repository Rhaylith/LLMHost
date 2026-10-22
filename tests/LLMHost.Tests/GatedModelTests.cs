using System.Net;
using LLMHostProject.Core;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class SecretStoreTests
{
    [Fact]
    public void ASecretRoundTripsAndIsNotReadableOnDisk()
    {
        string p = SecretStore.Protect("hf_abcdef123456");
        Assert.True(SecretStore.IsProtected(p));
        Assert.DoesNotContain("abcdef", p);
        Assert.Equal("hf_abcdef123456", SecretStore.Unprotect(p));
    }

    [Fact]
    public void ADamagedValueIsDroppedNotCrashed()
    {
        Assert.Null(SecretStore.Unprotect("dpapi:AAAA"));
        Assert.Null(SecretStore.Unprotect("dpapi:%%%not base64"));
        Assert.Equal("plain", SecretStore.Unprotect("plain"));            // legacy clear text passes through
    }
}

[Trait("Category", "Unit")]
[Collection("Settings")]
public class TokenStorageTests
{
    private static string File_ => AppPaths.SettingsFile;

    [Fact]
    public void TheTokenIsEncryptedInSettingsJsonAndPlainInMemory()
    {
        var s = AppSettings.Current;
        string? before = s.HuggingFaceToken;
        try
        {
            s.HuggingFaceToken = "hf_supersecret_token";
            s.Save();
            string text = File.ReadAllText(File_);
            Assert.DoesNotContain("hf_supersecret_token", text);
            Assert.Contains("dpapi:", text);
            Assert.Equal("hf_supersecret_token", s.HuggingFaceToken);          // saving does not change what the app uses
        }
        finally { s.HuggingFaceToken = before; s.Save(); }
    }

    [Fact]
    public void AClearTextTokenFromAnOlderVersionIsEncryptedOnFirstRead()
    {
        var s = AppSettings.Current;
        string? before = s.HuggingFaceToken;
        try
        {
            var loaded = new AppSettings { HuggingFaceToken = "hf_legacy_plain" };
            s.HuggingFaceToken = null; s.Save();
            File.WriteAllText(File_, File.ReadAllText(File_).Replace("{", "{\"huggingFaceToken\":\"hf_legacy_plain\",", StringComparison.Ordinal));
            loaded.OpenToken();                                                 // what Load does with a freshly read file
            Assert.Equal("hf_legacy_plain", loaded.HuggingFaceToken);
            Assert.DoesNotContain("hf_legacy_plain", File.ReadAllText(File_));
        }
        finally { s.HuggingFaceToken = before; s.Save(); }
    }
}

[Trait("Category", "Unit")]
[Collection("Settings")]
public class GatedDownloadTests : IDisposable
{
    private readonly HttpListener _l = new();
    private readonly string _url;
    private int _status = 403;

    public GatedDownloadTests()
    {
        int port;
        using (var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { t.Start(); port = ((IPEndPoint)t.LocalEndpoint).Port; }
        _url = $"http://127.0.0.1:{port}/gated/model.gguf";
        _l.Prefixes.Add($"http://127.0.0.1:{port}/");
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                c.Response.StatusCode = _status;
                c.Response.Close();
            }
        });
    }

    public void Dispose() { try { _l.Stop(); } catch { } }

    [Fact]
    public async Task ARefusedFileEndsInNeedsAccessAndCanBeRetried()
    {
        var m = new DownloadManager(Path.Combine(TestEnvironment.Home, "dl-gated.json"));
        var job = m.StartUrl(_url, "gatedpub");
        for (int i = 0; i < 100 && job.State is DownloadState.Queued or DownloadState.Downloading; i++) await Task.Delay(100);
        Assert.Equal(DownloadState.NeedsAccess, job.State);
        Assert.Contains("gated", job.Error!, StringComparison.OrdinalIgnoreCase);

        // retry is allowed from this state, and a second refusal lands in the same state again
        Assert.True(m.Resume(job.Id));
        for (int i = 0; i < 100 && job.State is DownloadState.Queued or DownloadState.Downloading; i++) await Task.Delay(100);
        Assert.Equal(DownloadState.NeedsAccess, job.State);
        Assert.Equal(1, m.ClearFinished());
    }

    [Fact]
    public void TheMessageSaysWhatToDoDependingOnTheToken()
    {
        Assert.Contains("accept its licence", new GatedModelException("o/n", hasToken: true).Message);
        Assert.Contains("set a token", new GatedModelException("o/n", hasToken: false).Message);
        Assert.Equal("https://huggingface.co/o/n", new GatedModelException("o/n").AccessUrl);
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class HfWhoAmIApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public HfWhoAmIApiTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task WithoutAnyTokenTheAnswerIsInvalidAndNothingIsSent()
    {
        var old = AppSettings.Current.HuggingFaceToken;
        var env = Environment.GetEnvironmentVariable("HF_TOKEN");
        try
        {
            AppSettings.Current.HuggingFaceToken = null;
            Environment.SetEnvironmentVariable("HF_TOKEN", null);
            var r = await _f.Http.PostAsync("/api/hf/whoami", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            var j = System.Text.Json.Nodes.JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
            Assert.False((bool)j["valid"]!);
            Assert.Contains("No token", (string)j["error"]!);
        }
        finally { AppSettings.Current.HuggingFaceToken = old; Environment.SetEnvironmentVariable("HF_TOKEN", env); }
    }
}
