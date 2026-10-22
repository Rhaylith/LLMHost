using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
[Collection("Settings")]
public class NotificationHubTests : IDisposable
{
    private readonly NotificationSettings _old = AppSettings.Current.Notifications;
    public NotificationHubTests() { AppSettings.Current.Notifications = new NotificationSettings(); NotificationHub.Clear(); }
    public void Dispose() { AppSettings.Current.Notifications = _old; NotificationHub.Clear(); }

    [Fact]
    public void AnEventIsRecordedAnnouncedAndFoundAgainBySinceId()
    {
        NotificationEvent? seen = null;
        void On(NotificationEvent e) => seen = e;
        NotificationHub.Published += On;
        try
        {
            var (before, _) = NotificationHub.Since(0);
            var e = NotificationHub.Publish("download", "Done", "model.gguf", "models")!;
            Assert.Equal(e, seen);
            Assert.Equal("models", e.View);
            var (last, events) = NotificationHub.Since(before);
            Assert.Equal(e.Id, last);
            Assert.Equal(new[] { e.Id }, events.Select(x => x.Id));
            Assert.Empty(NotificationHub.Since(last).Events);
        }
        finally { NotificationHub.Published -= On; }
    }

    [Theory]
    [InlineData("download", "downloads")]
    [InlineData("download-failed", "downloads")]
    [InlineData("model", "modelLoaded")]
    [InlineData("generation", "generation")]
    [InlineData("update", "updateAvailable")]
    public void EachKindFollowsItsOwnSwitch(string kind, string switchName)
    {
        var s = new NotificationSettings();
        Assert.NotNull(NotificationHub.Publish(kind, "t", "b", settings: s));
        typeof(NotificationSettings).GetProperties().First(p => string.Equals(p.Name, switchName, StringComparison.OrdinalIgnoreCase)).SetValue(s, false);
        Assert.Null(NotificationHub.Publish(kind, "t", "b", settings: s));                      // not even recorded
    }

    [Fact]
    public void TheMasterSwitchAndUnknownKindsStopEverythingButATestWorksWhenOn()
    {
        var off = new NotificationSettings { Enabled = false };
        Assert.Null(NotificationHub.Publish("download", "t", "b", settings: off));
        Assert.Null(NotificationHub.Publish("test", "t", "b", settings: off));
        Assert.Null(NotificationHub.Publish("nonsense", "t", "b"));
        Assert.NotNull(NotificationHub.Publish("test", "t", "b", settings: new NotificationSettings { Downloads = false, ModelLoaded = false }));
    }

    [Fact]
    public void OnlyTheLastHundredAreKept()
    {
        for (int i = 0; i < 130; i++) NotificationHub.Publish("test", "t" + i, "b");
        var (_, events) = NotificationHub.Since(0);
        Assert.Equal(100, events.Count);
        Assert.Equal("t129", events[^1].Title);
    }

    [Fact]
    public void TheSettingsAreValidated()
    {
        var s = new AppSettings(); s.Notifications.LoadSeconds = -1;
        Assert.Contains("notifications.loadSeconds", SettingsValidator.Validate(s).Select(e => e.Field));
        Assert.Equal(10, new NotificationSettings().LoadSeconds);
        Assert.True(new NotificationSettings().OnlyWhenMinimised);
    }
}

[Trait("Category", "Unit")]
[Collection("Settings")]
public class NotificationSourcesTests : IDisposable
{
    private readonly HttpListener _l = new();
    private readonly string _base;
    private int _status = 200;
    private readonly byte[] _data = Enumerable.Range(0, 3000).Select(i => (byte)(i % 200)).ToArray();

    public NotificationSourcesTests()
    {
        NotificationHub.Clear();
        int port;
        using (var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { t.Start(); port = ((IPEndPoint)t.LocalEndpoint).Port; }
        _base = $"http://127.0.0.1:{port}";
        _l.Prefixes.Add(_base + "/");
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                c.Response.StatusCode = _status;
                if (_status == 200) { c.Response.ContentLength64 = _data.Length; await c.Response.OutputStream.WriteAsync(_data); }
                c.Response.Close();
            }
        });
    }

    public void Dispose() { try { _l.Stop(); } catch { } NotificationHub.Clear(); UpdateService.FeedFetcher = null; }

    private static async Task Finish(DownloadJob j) { for (int i = 0; i < 100 && j.State is DownloadState.Queued or DownloadState.Downloading; i++) await Task.Delay(100); }

    [Fact]
    public async Task AFinishedDownloadAnnouncesItselfAndPointsAtTheModels()
    {
        var m = new DownloadManager(Path.Combine(TestEnvironment.Home, "dl-notify.json"));
        var (before, _) = NotificationHub.Since(0);
        var job = m.StartUrl(_base + "/n/notify-me.gguf", "notifypub");
        await Finish(job);
        Assert.Equal(DownloadState.Completed, job.State);
        var e = NotificationHub.Since(before).Events.Single(x => x.Kind == "download");
        Assert.Equal("notify-me.gguf", e.Body);
        Assert.Equal("models", e.View);
    }

    [Fact]
    public async Task AFailedDownloadAnnouncesTheReason()
    {
        _status = 404;
        var m = new DownloadManager(Path.Combine(TestEnvironment.Home, "dl-notify2.json"));
        var (before, _) = NotificationHub.Since(0);
        var job = m.StartUrl(_base + "/n/missing-one.gguf", "notifypub2");
        await Finish(job);
        Assert.Equal(DownloadState.Failed, job.State);
        var e = NotificationHub.Since(before).Events.Single(x => x.Kind == "download-failed");
        Assert.Contains("missing-one.gguf", e.Body);
        Assert.Equal("discover", e.View);
    }

    [Fact]
    public async Task AnUpdateIsAnnouncedOncePerVersion()
    {
        var oldFeed = AppSettings.Current.UpdateFeedUrl;
        try
        {
            AppSettings.Current.UpdateFeedUrl = "https://example.com/r";
            UpdateService.FeedFetcher = (_, _) => Task.FromResult("""[{"tag_name":"v77.1.0","assets":[]}]""");
            var (before, _) = NotificationHub.Since(0);
            await UpdateService.CheckAsync(currentOverride: "1.0.0");
            await UpdateService.CheckAsync(currentOverride: "1.0.0");
            var events = NotificationHub.Since(before).Events.Where(x => x.Kind == "update").ToList();
            Assert.Single(events);
            Assert.Contains("77.1.0", events[0].Body);
            Assert.Equal("settings", events[0].View);
        }
        finally { AppSettings.Current.UpdateFeedUrl = oldFeed; }
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class NotificationApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public NotificationApiTests(ServerFixture f) => _f = f;
    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task AClientCanReportAnAnswerAndTheEventComesBackWithTheSettings()
    {
        var first = JsonNode.Parse(await _f.Http.GetStringAsync("/api/notifications?since=0"))!;
        long last = (long)first["last"]!;
        var r = JsonNode.Parse(await (await _f.Http.PostAsync("/api/notifications", Body("""{"kind":"generation","title":"Your answer is ready","body":"My chat","view":"models"}"""))).Content.ReadAsStringAsync())!;
        Assert.True((bool)r["published"]!);
        var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/notifications?since=" + last))!;
        var e = j["events"]!.AsArray().Single()!;
        Assert.Equal("generation", (string)e["kind"]!);
        Assert.Equal("My chat", (string)e["body"]!);
        Assert.Equal("models", (string)e["view"]!);
        Assert.True((bool)j["settings"]!["enabled"]!);
    }

    [Fact]
    public async Task ClientsMayOnlyReportTheirOwnKindsAndInvalidViewsFallBackToTheChat()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.PostAsync("/api/notifications", Body("""{"kind":"update","title":"fake"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Http.PostAsync("/api/notifications", Body("""{"kind":"download","title":"fake"}"""))).StatusCode);
        long last = (long)JsonNode.Parse(await _f.Http.GetStringAsync("/api/notifications"))!["last"]!;
        await _f.Http.PostAsync("/api/notifications", Body("""{"kind":"test","title":"t","view":"../etc"}"""));
        var e = JsonNode.Parse(await _f.Http.GetStringAsync("/api/notifications?since=" + last))!["events"]![0]!;
        Assert.Equal("chat", (string)e["view"]!);
    }

    [Fact]
    public async Task ASwitchedOffKindIsNotPublished()
    {
        var old = AppSettings.Current.Notifications;
        try
        {
            AppSettings.Current.Notifications = new NotificationSettings { Generation = false };
            var r = JsonNode.Parse(await (await _f.Http.PostAsync("/api/notifications", Body("""{"kind":"generation","title":"x"}"""))).Content.ReadAsStringAsync())!;
            Assert.False((bool)r["published"]!);
        }
        finally { AppSettings.Current.Notifications = old; }
    }

    [Fact]
    public async Task ThePageHasTheCardAndThePoller()
    {
        string html = await _f.Http.GetStringAsync("/");
        Assert.Contains("id=\"notify-card\"", html);
        string js = await _f.Http.GetStringAsync("/ui/notifications.js");
        Assert.Contains("new Notification(", js);
        Assert.Contains("function openView", js);
        Assert.Contains("IN_SHELL", js);                                                  // the window shows them from the tray instead
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class ModelLoadNotificationTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public ModelLoadNotificationTests(ServerFixture f) => _f = f;

    [VlmFact]
    public async Task ASlowEnoughLoadAnnouncesTheModel()
    {
        var old = AppSettings.Current.Notifications;
        try
        {
            AppSettings.Current.Notifications = new NotificationSettings { LoadSeconds = 0 };
            var (before, _) = NotificationHub.Since(0);
            _f.Host.Models.UnloadAll();
            await _f.Host.Models.LoadAsync(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!, new LoadOptions { Device = "cpu", ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Vision = false, Identifier = "notify-load" });
            var e = NotificationHub.Since(before).Events.Single(x => x.Kind == "model");
            Assert.Contains("notify-load", e.Body);
            Assert.Equal("chat", e.View);

            _f.Host.Models.UnloadAll();
            AppSettings.Current.Notifications = new NotificationSettings { LoadSeconds = 3600 };
            var (b2, _) = NotificationHub.Since(0);
            await _f.Host.Models.LoadAsync(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!, new LoadOptions { Device = "cpu", ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Vision = false, Identifier = "notify-quick" });
            Assert.DoesNotContain(NotificationHub.Since(b2).Events, x => x.Kind == "model");           // a quick load is not worth a notification
        }
        finally { AppSettings.Current.Notifications = old; _f.Host.Models.UnloadAll(); }
    }
}
