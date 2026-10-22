using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class SemVerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0.0-1", "1.0.0-alpha", -1)]
    [InlineData("1.0.0-beta", "1.0.0-beta.1", -1)]
    [InlineData("v1.4.0+build5", "1.4.0", 0)]
    [InlineData("1.4", "1.4.0", 0)]
    public void VersionsOrderAsInSemver(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(SemVer.Parse(a).CompareTo(SemVer.Parse(b))));

    [Theory]
    [InlineData("1.2.3", true)] [InlineData("v2.0.0-rc.1", true)] [InlineData("1.0.0.0", true)] [InlineData("latest", false)] [InlineData("", false)] [InlineData(null, false)]
    public void OnlyVersionsParse(string? text, bool ok) => Assert.Equal(ok, SemVer.TryParse(text, out _));

    [Fact]
    public void PreReleasesAreMarkedAndPrintedBack()
    {
        Assert.True(SemVer.Parse("1.2.3-beta.1").IsPrerelease);
        Assert.False(SemVer.Parse("1.2.3").IsPrerelease);
        Assert.Equal("1.2.3-beta.1", SemVer.Parse("v1.2.3-beta.1+x").ToString());
    }
}

[Trait("Category", "Unit")]
public class ReleaseFeedTests
{
    private const string GitHub = """
        [
          {"tag_name":"v1.3.0-beta.1","prerelease":true,"draft":false,"body":"beta notes","published_at":"2026-10-01T10:00:00Z","assets":[
             {"name":"LLMHost-1.3.0-beta.1-win-x64.zip","browser_download_url":"https://github.com/o/r/releases/download/v1.3.0-beta.1/LLMHost-1.3.0-beta.1-win-x64.zip","size":100,"digest":"sha256:ABCD"}]},
          {"tag_name":"v1.2.0","prerelease":false,"body":"stable notes","assets":[
             {"name":"LLMHost-1.2.0-win-x64.zip","browser_download_url":"https://github.com/o/r/releases/download/v1.2.0/LLMHost-1.2.0-win-x64.zip","size":90},
             {"name":"LLMHost-1.2.0-win-x64.msi","browser_download_url":"https://github.com/o/r/releases/download/v1.2.0/x.msi","size":80},
             {"name":"LLMHost-1.2.0-linux-x64.tar.gz","browser_download_url":"https://github.com/o/r/releases/download/v1.2.0/l.tgz","size":70},
             {"name":"SHA256SUMS","browser_download_url":"https://github.com/o/r/releases/download/v1.2.0/SHA256SUMS","size":10}]},
          {"tag_name":"v1.1.0","prerelease":false,"assets":[]},
          {"tag_name":"v9.9.9","draft":true,"assets":[]},
          {"tag_name":"not-a-version","assets":[]}
        ]
        """;

    [Fact]
    public void AGitHubReleasesListIsRead()
    {
        var r = ReleaseFeed.Parse(GitHub);
        Assert.Equal(new[] { "1.3.0-beta.1", "1.2.0", "1.1.0" }, r.Select(x => x.Version));             // drafts and non-versions are skipped
        Assert.True(r[0].Prerelease); Assert.False(r[1].Prerelease);
        Assert.Equal("abcd", r[0].Assets[0].Sha256);                                                     // "digest": "sha256:..."
        Assert.Equal("stable notes", r[1].Notes);
        Assert.Equal(2026, r[0].Published!.Value.Year);
    }

    [Fact]
    public void OurReleasesJsonAndTheOldSingleVersionDocumentAreRead()
    {
        var r = ReleaseFeed.Parse("""{"releases":[{"version":"2.0.0","channel":"stable","notes":"n","assets":[{"name":"a.zip","url":"https://x/a.zip","sha256":"AB12","size":5}]},{"version":"2.1.0","channel":"beta"}]}""");
        Assert.Equal(2, r.Count);
        Assert.Equal("ab12", r[0].Assets[0].Sha256);
        Assert.True(r[1].Prerelease);
        var old = ReleaseFeed.Parse("""{"version":"1.5.0","url":"https://example.com/LLMHost-1.5.0.zip","notes":"hi"}""");
        Assert.Single(old);
        Assert.Equal("LLMHost-1.5.0.zip", old[0].Assets[0].Name);
        Assert.Empty(ReleaseFeed.Parse("{}"));
        Assert.Empty(ReleaseFeed.Parse("[]"));
    }

    [Fact]
    public void TheChannelDecidesWhichReleaseIsNewest()
    {
        var r = ReleaseFeed.Parse(GitHub);
        Assert.Equal("1.2.0", ReleaseFeed.Newest(r, "stable", SemVer.Parse("1.1.0"))!.Version);
        Assert.Equal("1.3.0-beta.1", ReleaseFeed.Newest(r, "beta", SemVer.Parse("1.1.0"))!.Version);
        Assert.Null(ReleaseFeed.Newest(r, "stable", SemVer.Parse("1.2.0")));                             // already there
        Assert.Null(ReleaseFeed.Newest(r, "beta", SemVer.Parse("1.3.0")));                               // a stable 1.3.0 is newer than 1.3.0-beta.1
        Assert.Equal("1.3.0-beta.1", ReleaseFeed.Newest(r, "beta", SemVer.Parse("1.2.0"))!.Version);
    }

    [Fact]
    public void TheZipIsPreferredThenTheMsiAndOtherPlatformsAreNeverChosen()
    {
        var r = ReleaseFeed.Parse(GitHub);
        Assert.Equal("LLMHost-1.2.0-win-x64.zip", ReleaseFeed.PickAsset(r[1])!.Name);
        var noZip = r[1] with { Assets = r[1].Assets.Where(a => !a.Name.EndsWith(".zip")).ToList() };
        Assert.EndsWith(".msi", ReleaseFeed.PickAsset(noZip)!.Name);
        var onlyLinux = r[1] with { Assets = r[1].Assets.Where(a => a.Name.Contains("linux") || a.Name == "SHA256SUMS").ToList() };
        Assert.Null(ReleaseFeed.PickAsset(onlyLinux));
        Assert.Null(ReleaseFeed.PickAsset(r[2]));
    }

    [Fact]
    public void ChecksumFilesAreParsed()
    {
        var map = ReleaseFeed.ParseChecksums($"{new string('A', 64)}  LLMHost-1.2.0-win-x64.zip\n{new string('b', 64)} *other.msi\nnot a line\n");
        Assert.Equal(new string('a', 64), map["LLMHost-1.2.0-win-x64.zip"]);
        Assert.Equal(new string('b', 64), map["other.msi"]);
        Assert.Equal(2, map.Count);
    }
}

[Trait("Category", "Unit")]
[Collection("Settings")]
public class UpdateServiceTests : IDisposable
{
    private readonly string _oldFeed = AppSettings.Current.UpdateFeedUrl ?? ""; private readonly string _oldChannel = AppSettings.Current.Update.Channel; private readonly bool _oldSig = AppSettings.Current.Update.RequireSignature;
    public UpdateServiceTests() { AppSettings.Current.UpdateFeedUrl = "https://example.com/releases"; }
    public void Dispose() { AppSettings.Current.UpdateFeedUrl = _oldFeed == "" ? null : _oldFeed; AppSettings.Current.Update.Channel = _oldChannel; AppSettings.Current.Update.RequireSignature = _oldSig; UpdateService.FeedFetcher = null; }

    private static string Feed(string hash = "") => $$"""
        [{"tag_name":"v2.0.0","assets":[{"name":"LLMHost-2.0.0-win-x64.zip","browser_download_url":"https://h/LLMHost-2.0.0-win-x64.zip","size":10{{hash}}},{"name":"SHA256SUMS","browser_download_url":"https://h/SHA256SUMS"}]},
          {"tag_name":"v2.1.0-beta.1","prerelease":true,"assets":[{"name":"LLMHost-2.1.0-beta.1-win-x64.zip","browser_download_url":"https://h/b.zip","sha256":"ff"}]}]
        """;

    [Fact]
    public async Task TheChannelAndTheCurrentVersionDecideTheOffer()
    {
        UpdateService.FeedFetcher = (url, _) => Task.FromResult(url.EndsWith("SHA256SUMS") ? $"{new string('c', 64)}  LLMHost-2.0.0-win-x64.zip\n" : Feed());
        AppSettings.Current.Update.Channel = "stable";
        var u = await UpdateService.CheckAsync(currentOverride: "1.0.0");
        Assert.Equal("2.0.0", u!.Version);
        Assert.False(u.Prerelease);
        Assert.Equal(new string('c', 64), u.Asset!.Sha256);                                            // taken from SHA256SUMS
        Assert.Null(await UpdateService.CheckAsync(currentOverride: "2.0.0"));
        Assert.Null(UpdateService.LastError);

        AppSettings.Current.Update.Channel = "beta";
        Assert.Equal("2.1.0-beta.1", (await UpdateService.CheckAsync(currentOverride: "2.0.0"))!.Version);
    }

    [Fact]
    public async Task WithoutAFeedOrWithABrokenOneNothingIsOfferedAndTheErrorIsKept()
    {
        AppSettings.Current.UpdateFeedUrl = null;
        Assert.Null(await UpdateService.CheckAsync());
        Assert.Contains("No update feed", UpdateService.LastError);
        AppSettings.Current.UpdateFeedUrl = "https://example.com/x";
        UpdateService.FeedFetcher = (_, _) => throw new HttpRequestException("offline");
        Assert.Null(await UpdateService.CheckAsync());
        Assert.Contains("offline", UpdateService.LastError);
        UpdateService.FeedFetcher = (_, _) => Task.FromResult("not json");
        Assert.Null(await UpdateService.CheckAsync());
        Assert.NotNull(UpdateService.LastError);
    }

    private sealed class Stub : HttpMessageHandler
    {
        public byte[] Body = Array.Empty<byte>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) });
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task ADownloadMustMatchItsChecksum()
    {
        var body = Encoding.UTF8.GetBytes("pretend this is a zip");
        using var http = new HttpClient(new Stub { Body = body });
        string path = await UpdateService.DownloadAsync(new ReleaseAsset("LLMHost-9.zip", "https://h/x.zip", Hash(body), body.Length), client: http);
        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);

        var bad = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.DownloadAsync(new ReleaseAsset("LLMHost-9.zip", "https://h/x.zip", new string('0', 64), 0), client: http));
        Assert.Contains("SHA-256", bad.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.DownloadAsync(new ReleaseAsset("x.zip", "https://h/x.zip", null, 0), client: http));          // nothing to verify against
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateService.DownloadAsync(new ReleaseAsset("x.zip", "http://h/x.zip", Hash(body), 0)));                    // plain http
    }

    [Fact]
    public async Task AnUnsignedInstallerIsRefusedEvenWithTheRightChecksum()
    {
        var body = Encoding.UTF8.GetBytes("not a real msi");
        using var http = new HttpClient(new Stub { Body = body });
        AppSettings.Current.Update.RequireSignature = true;
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.DownloadAsync(new ReleaseAsset("LLMHost-9.msi", "https://h/x.msi", Hash(body), 0), client: http));
        Assert.Contains("signature", ex.Message);
        AppSettings.Current.Update.RequireSignature = false;
        string path = await UpdateService.DownloadAsync(new ReleaseAsset("LLMHost-9.msi", "https://h/x.msi", Hash(body), 0), client: http);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
        Assert.False(UpdateService.HasValidSignature(typeof(UpdateServiceTests).Assembly.Location.Replace(".dll", ".pdb")));      // a file without a signature
    }

    [Theory]
    [InlineData(UpdateService.RunMode.Window, null, "\"C:\\app\\llmhost.exe\"")]
    [InlineData(UpdateService.RunMode.Daemon, "\"C:\\app\\llmhost.exe\" daemon down", "\"C:\\app\\llmhost.exe\" daemon up")]
    [InlineData(UpdateService.RunMode.Service, "sc stop LLMHost", "sc start LLMHost")]
    public void ThePlanStopsAndStartsWhatWasRunning(UpdateService.RunMode mode, string? before, string after)
    {
        var plan = UpdateService.BuildPlan(@"C:\tmp\LLMHost-2.zip", "2.0.0", mode, @"C:\app", @"C:\app\llmhost.exe");
        Assert.Equal(before, plan.BeforeCommand);
        Assert.Equal(after, plan.AfterCommand);
        Assert.Equal("zip", plan.Kind);
        Assert.Equal(Environment.ProcessId, plan.WaitPid);
        Assert.Equal(@"C:\app", plan.TargetDir);
    }
}

[Trait("Category", "Unit")]
public class UpdateApplyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmhost-apply-" + Guid.NewGuid().ToString("N")[..8]);
    private string App => Path.Combine(_root, "app");
    public UpdateApplyTests() { Directory.CreateDirectory(App); UpdateApply.RestartGrace = TimeSpan.FromMilliseconds(10); }
    public void Dispose() { UpdateApply.RestartGrace = TimeSpan.FromSeconds(8); try { Directory.Delete(_root, true); } catch { } try { Directory.Delete(App + ".update", true); } catch { } }

    private sealed class FakeEnv : IUpdateEnv
    {
        public List<string> Log_ = new(), Ran = new(), Started = new();
        public bool Exits = true, Survives = true; public int ExitCode;
        public bool WaitForExit(int pid, TimeSpan t) { Log_.Add("wait " + pid); return Exits; }
        public int Run(string c, TimeSpan t) { Ran.Add(c); return ExitCode; }
        public bool StartAndSurvives(string c, TimeSpan g) { Started.Add(c); return Survives || Started.Count > 1; }
        public void Log(string m) => Log_.Add(m);
    }

    private string Zip(Dictionary<string, string> files, string? root = null)
    {
        string path = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N")[..4] + ".zip");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files) { using var w = new StreamWriter(z.CreateEntry((root != null ? root + "/" : "") + name).Open()); w.Write(text); }
        return path;
    }

    private static string Read(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel));

    private void Install(string version) { File.WriteAllText(Path.Combine(App, "llmhost.exe"), "old exe " + version); File.WriteAllText(Path.Combine(App, "lib.dll"), "old lib"); File.WriteAllText(Path.Combine(App, "user-notes.txt"), "mine"); }

    [Fact]
    public void FilesAreReplacedNewOnesAddedAndOtherFilesLeftAlone()
    {
        Install("1");
        var pkg = Zip(new() { ["llmhost.exe"] = "new exe", ["lib.dll"] = "new lib", ["extra/new.dll"] = "added" });
        var env = new FakeEnv();
        var r = UpdateApply.Apply(new UpdatePlan(App, pkg, "zip", 1234, "stop it", "\"" + Path.Combine(App, "llmhost.exe") + "\"", "2.0.0"), env);
        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal("new exe", Read(App, "llmhost.exe"));
        Assert.Equal("new lib", Read(App, "lib.dll"));
        Assert.Equal("added", Read(App, @"extra\new.dll"));
        Assert.Equal("mine", Read(App, "user-notes.txt"));                                             // not part of the package: kept
        Assert.Equal(new[] { "stop it" }, env.Ran);                                                    // the before-command ran first
        Assert.Contains("wait 1234", env.Log_);
        Assert.Single(env.Started);
        Assert.Equal("old exe 1", Read(App + ".backup", "llmhost.exe"));                               // what was replaced is kept for a while
        Assert.False(Directory.Exists(App + ".update"));
    }

    [Fact]
    public void APackageWithOneTopFolderIsUnpackedWithoutIt()
    {
        Install("1");
        var pkg = Zip(new() { ["llmhost.exe"] = "from folder" }, root: "LLMHost");
        Assert.Equal(UpdateOutcome.Updated, UpdateApply.Apply(new UpdatePlan(App, pkg, "zip", 0, null, null), new FakeEnv()).Outcome);
        Assert.Equal("from folder", Read(App, "llmhost.exe"));
        Assert.False(Directory.Exists(Path.Combine(App, "LLMHost")));
    }

    [Fact]
    public void ABrokenPackageChangesNothing()
    {
        Install("1");
        var noProgram = Zip(new() { ["readme.txt"] = "no exe in here" });
        var r = UpdateApply.Apply(new UpdatePlan(App, noProgram, "zip", 0, null, null), new FakeEnv());
        Assert.Equal(UpdateOutcome.FailedNothingChanged, r.Outcome);
        Assert.Contains("llmhost.exe", r.Message);
        Assert.Equal("old exe 1", Read(App, "llmhost.exe"));

        string notZip = Path.Combine(_root, "bad.zip"); File.WriteAllText(notZip, "garbage");
        Assert.Equal(UpdateOutcome.FailedNothingChanged, UpdateApply.Apply(new UpdatePlan(App, notZip, "zip", 0, null, null), new FakeEnv()).Outcome);
        Assert.Equal(UpdateOutcome.FailedNothingChanged, UpdateApply.Apply(new UpdatePlan(App, Path.Combine(_root, "missing.zip"), "zip", 0, null, null), new FakeEnv()).Outcome);
        Assert.Equal("old exe 1", Read(App, "llmhost.exe"));
    }

    [Fact]
    public void AZipSlipPathIsRefused()
    {
        Install("1");
        string path = Path.Combine(_root, "slip.zip");
        using (var z = ZipFile.Open(path, ZipArchiveMode.Create)) { using (var w = new StreamWriter(z.CreateEntry("llmhost.exe").Open())) w.Write("x"); using var w2 = new StreamWriter(z.CreateEntry("../../escaped.txt").Open()); w2.Write("bad"); }
        var r = UpdateApply.Apply(new UpdatePlan(App, path, "zip", 0, null, null), new FakeEnv());
        Assert.Equal(UpdateOutcome.FailedNothingChanged, r.Outcome);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.Equal("old exe 1", Read(App, "llmhost.exe"));
    }

    [Fact]
    public void IfTheAppDoesNotCloseNothingIsTouched()
    {
        Install("1");
        var r = UpdateApply.Apply(new UpdatePlan(App, Zip(new() { ["llmhost.exe"] = "new" }), "zip", 99, null, null), new FakeEnv { Exits = false });
        Assert.Equal(UpdateOutcome.FailedNothingChanged, r.Outcome);
        Assert.Equal("old exe 1", Read(App, "llmhost.exe"));
    }

    [Fact]
    public void IfTheNewVersionDoesNotStayUpTheOldFilesComeBackAndNewOnesGo()
    {
        Install("1");
        var pkg = Zip(new() { ["llmhost.exe"] = "crashing exe", ["lib.dll"] = "new lib", ["new-only.dll"] = "x" });
        var env = new FakeEnv { Survives = false };
        var r = UpdateApply.Apply(new UpdatePlan(App, pkg, "zip", 0, null, "start", "2.0.0"), env);
        Assert.Equal(UpdateOutcome.FailedRolledBack, r.Outcome);
        Assert.Equal("old exe 1", Read(App, "llmhost.exe"));
        Assert.Equal("old lib", Read(App, "lib.dll"));
        Assert.False(File.Exists(Path.Combine(App, "new-only.dll")));
        Assert.Equal(2, env.Started.Count);                                                            // the old program was started again
    }

    [Fact]
    public void ALockedFileMeansARollbackOfWhatWasAlreadyCopied()
    {
        Install("1");
        File.WriteAllText(Path.Combine(App, "zz-locked.dll"), "old locked");
        var pkg = Zip(new() { ["llmhost.exe"] = "new exe", ["zz-locked.dll"] = "new locked" });
        using var hold = new FileStream(Path.Combine(App, "zz-locked.dll"), FileMode.Open, FileAccess.Read, FileShare.None);      // the app (or a scanner) still has it open
        var env = new FakeEnv();
        var r = UpdateApply.Apply(new UpdatePlan(App, pkg, "zip", 0, null, "start", "2.0.0"), env);
        Assert.Equal(UpdateOutcome.FailedRolledBack, r.Outcome);
        hold.Dispose();
        Assert.Equal("old exe 1", Read(App, "llmhost.exe"));
        Assert.Equal("old locked", Read(App, "zz-locked.dll"));
        Assert.Single(env.Started);                                                                    // the old version was started again
    }

    [Theory]
    [InlineData("msi", 0, UpdateOutcome.Updated, "msiexec")]
    [InlineData("msi", 3010, UpdateOutcome.Updated, "msiexec")]
    [InlineData("msi", 1603, UpdateOutcome.FailedNothingChanged, "msiexec")]
    [InlineData("exe", 0, UpdateOutcome.Updated, "VERYSILENT")]
    public void InstallersAreRunSilentlyAndTheirExitCodeDecides(string kind, int code, UpdateOutcome expected, string contains)
    {
        string pkg = Path.Combine(_root, "setup." + kind); File.WriteAllText(pkg, "x");
        var env = new FakeEnv { ExitCode = code };
        Assert.Equal(expected, UpdateApply.Apply(new UpdatePlan(App, pkg, kind, 0, null, null), env).Outcome);
        Assert.Contains(contains, env.Ran.Single());
    }

    [Fact]
    public async Task TheRealUpdaterProgramReplacesAFolderWhenStartedOnItsOwn()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "LLMHost.sln"))) dir = Path.GetDirectoryName(dir);
        string? dll = dir == null ? null : Directory.GetFiles(Path.Combine(dir, "bin"), "LLMHost.Updater.dll", SearchOption.AllDirectories).FirstOrDefault();
        if (dll == null) return;                                                                        // the application was not built here

        Install("1");
        var pkg = Zip(new() { ["llmhost.exe"] = "updated by the real program", ["lib.dll"] = "new lib" });
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" --target \"{App}\" --package \"{pkg}\" --kind zip --pid 0 --version 9.9.9") { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        Assert.Equal("updated by the real program", Read(App, "llmhost.exe"));
        Assert.Contains("Updated", File.ReadAllText(Path.Combine(Path.GetTempPath(), "llmhost-update.log")));

        var broken = Zip(new() { ["notes.txt"] = "nothing to run" });
        using var q = Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\" --target \"{App}\" --package \"{broken}\" --kind zip --pid 0") { UseShellExecute = false, CreateNoWindow = true })!;
        await q.WaitForExitAsync();
        Assert.Equal(2, q.ExitCode);                                                                    // failed, nothing changed
        Assert.Equal("updated by the real program", Read(App, "llmhost.exe"));
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class UpdateApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public UpdateApiTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task StatusCheckAndInstallFollowTheFeed()
    {
        var oldFeed = AppSettings.Current.UpdateFeedUrl;
        try
        {
            AppSettings.Current.UpdateFeedUrl = "https://example.com/releases";
            UpdateService.FeedFetcher = (_, _) => Task.FromResult("""[{"tag_name":"v99.0.0","body":"big news","assets":[{"name":"LLMHost-99.0.0-win-x64.zip","browser_download_url":"https://h/z.zip","size":10}]}]""");
            var checkRes = JsonNode.Parse(await (await _f.Http.PostAsync("/api/update/check", new StringContent("{}"))).Content.ReadAsStringAsync())!;
            Assert.Equal("99.0.0", (string)checkRes["latest"]!["version"]!);
            Assert.Equal("big news", (string)checkRes["latest"]!["notes"]!);
            Assert.False((bool)checkRes["latest"]!["canInstall"]!);                                  // the feed gave no checksum
            Assert.Equal("stable", (string)checkRes["channel"]!);
            Assert.True((bool)JsonNode.Parse(await _f.Http.GetStringAsync("/api/update"))!["feedConfigured"]!);

            var install = await _f.Http.PostAsync("/api/update/install", new StringContent("{}"));          // refused: nothing to verify the package with
            Assert.Equal(HttpStatusCode.BadRequest, install.StatusCode);
            Assert.Contains("verified", await install.Content.ReadAsStringAsync());
        }
        finally { AppSettings.Current.UpdateFeedUrl = oldFeed; UpdateService.FeedFetcher = null; }
    }

    [Fact]
    public async Task TheChannelIsValidatedAndSaved()
    {
        var s = JsonNode.Parse(await _f.Http.GetStringAsync("/api/settings"))!;
        s["update"] = new JsonObject { ["channel"] = "nightly" };
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.PutAsync("/api/settings", new StringContent(s.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task ThePackagingFilesExistAndAgreeWithTheApp()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "LLMHost.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        string workflow = File.ReadAllText(Path.Combine(dir!, ".github", "workflows", "release.yml"));
        Assert.Contains("SHA256SUMS", workflow);
        Assert.Contains("prerelease", workflow);
        string profile = File.ReadAllText(Path.Combine(dir!, "Properties", "PublishProfiles", "win-x64-single.pubxml"));
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", profile);
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>false", profile);
        Assert.Contains("LocalAppDataFolder", File.ReadAllText(Path.Combine(dir!, "installer", "Package.wxs")));
        Assert.Contains("SHA256SUMS", File.ReadAllText(Path.Combine(dir!, "build", "publish.ps1")));
        await Task.CompletedTask;
    }
}
