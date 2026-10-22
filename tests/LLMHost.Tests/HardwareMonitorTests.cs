using System.Text.Json.Nodes;
using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class GpuCounterNameTests
{
    [Fact]
    public void EngineNamesAreParsed()
    {
        var p = GpuCounterName.Parse("pid_1234_luid_0x00000000_0x0000F5A3_phys_0_eng_2_engtype_3D");
        Assert.NotNull(p);
        Assert.Equal(1234, p!.Pid);
        Assert.Equal(0xF5A3L, p.Luid);
        Assert.Equal(0, p.Phys);
        Assert.Equal(2, p.Engine);
        Assert.Equal("3D", p.EngineType);
    }

    [Fact]
    public void AdapterAndProcessMemoryNamesAreParsed()
    {
        var a = GpuCounterName.Parse("luid_0x00000001_0x0000F5A3_phys_0");
        Assert.NotNull(a);
        Assert.Null(a!.Pid);
        Assert.Null(a.Engine);
        Assert.Equal((1L << 32) | 0xF5A3L, a.Luid);               // the high part sits in the upper 32 bits, like DXGI's LUID

        var p = GpuCounterName.Parse("pid_77_luid_0x00000000_0x00010A2B_phys_0");
        Assert.Equal(77, p!.Pid);
        Assert.Equal(0x10A2BL, p.Luid);
    }

    [Fact]
    public void ANegativeHighPartKeepsTheSignLikeDxgi()
    {
        var p = GpuCounterName.Parse("luid_0xFFFFFFFF_0x00000002_phys_0");
        Assert.Equal(((long)-1 << 32) | 2L, p!.Luid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pid_x_luid_0x0_0x0_phys_0")]
    [InlineData("luid_0x00000000_0x0000F5A3")]
    public void OtherNamesAreRejected(string name) => Assert.Null(GpuCounterName.Parse(name));

    [Fact]
    public void NullIsRejected() => Assert.Null(GpuCounterName.Parse(null));

    [Fact]
    public void UtilisationAddsProcessesPerEngineAndTakesTheBusiestEngine()
    {
        const string a = "luid_0x00000000_0x0000AAAA_phys_0";
        var rows = new (string, double)[]
        {
            ($"pid_1_{a}_eng_0_engtype_3D", 30), ($"pid_2_{a}_eng_0_engtype_3D", 25),      // engine 0: 55 in total
            ($"pid_1_{a}_eng_1_engtype_Copy", 70),                                          // engine 1: 70 -> the busiest
            ($"pid_1_{a}_eng_2_engtype_Compute", 5),
            ("pid_9_luid_0x00000000_0x0000BBBB_phys_0_eng_0_engtype_3D", 12),               // another adapter
            ("not a counter", 99),
        };
        var u = GpuCounterName.AdapterUtilization(rows);
        Assert.Equal(70, u[0xAAAA]);
        Assert.Equal(12, u[0xBBBB]);
        Assert.Equal(2, u.Count);
    }

    [Fact]
    public void UtilisationNeverExceedsAHundred()
    {
        var u = GpuCounterName.AdapterUtilization(new[]
        {
            ("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 80.0), ("pid_2_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 80.0),
        });
        Assert.Equal(100, u[1]);
    }
}

[Trait("Category", "Api")]
[Collection("Settings")]
public class HardwareLiveApiTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _f;
    public HardwareLiveApiTests(ServerFixture f) => _f = f;

    [Fact]
    public async Task LiveEndpointAlwaysReturnsTheFullSchema()
    {
        var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/hardware/live"))!;
        // values may be null when Windows has no such counter, but the shape is always there
        Assert.True(j.AsObject().ContainsKey("cpuPercent"));
        Assert.True((long)j["ramTotalBytes"]! > 0);
        Assert.True((long)j["ramUsedBytes"]! >= 0);
        Assert.True((long)j["process"]!["workingSetBytes"]! > 0);
        Assert.NotNull(j["gpus"]!.AsArray());
        Assert.NotNull(j["models"]!.AsArray());
        Assert.NotNull(j["notes"]!.AsArray());
        foreach (var g in j["gpus"]!.AsArray())
            foreach (var key in new[] { "index", "name", "utilization", "dedicatedUsedBytes", "dedicatedTotalBytes", "temperatureC" })
                Assert.True(g!.AsObject().ContainsKey(key), $"gpu entry lacks '{key}'");
        var cpu = j["cpuPercent"]?.GetValue<double>();
        if (cpu.HasValue) Assert.InRange(cpu.Value, 0, 100);
    }

    [Fact]
    public async Task EveryDetectedGpuIsListedAndMapsToACounterAdapter()
    {
        var hw = Hardware.Detect();
        var j = JsonNode.Parse(await _f.Http.GetStringAsync("/api/hardware/live"))!;
        Assert.Equal(hw.Gpus.Count, j["gpus"]!.AsArray().Count);
        // DXGI gives every real adapter a LUID; without it no counter could be matched to the GPU
        Assert.All(hw.Gpus, g => Assert.NotEqual(0L, g.Luid));
    }

    [Fact]
    public async Task TheStreamSendsAReadingEverySecondAndStopsWhenTheClientLeaves()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/hardware/stream");
        using var resp = await _f.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));

        var times = new List<DateTime>();
        while (times.Count < 3)
        {
            string? line = await reader.ReadLineAsync(cts.Token);
            Assert.NotNull(line);
            if (!line!.StartsWith("data: ")) continue;
            var j = JsonNode.Parse(line[6..])!;
            Assert.True(j["gpus"] != null && j["process"] != null);
            times.Add(DateTime.UtcNow);
        }
        Assert.True((times[2] - times[0]).TotalSeconds >= 1.5, "readings should arrive about once a second");

        Assert.True(HardwareMonitor.Shared.Listeners >= 1);
        resp.Dispose();
        for (int i = 0; i < 50 && HardwareMonitor.Shared.Listeners > 0; i++) await Task.Delay(100);
        Assert.Equal(0, HardwareMonitor.Shared.Listeners);                       // sampling stops when nobody listens
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class HardwareMonitorIntegrationTests
{
    [VlmFact]
    public async Task AModelOnTheGpuShowsUpInTheProcessMemoryOfThatAdapter()
    {
        LLMHostProject.Inference.NativeBackend.Initialize();
        if (!LLMHostProject.Inference.NativeBackend.GpuAvailable) return;
        var model = new LLMHostProject.Models.ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!)!;
        var o = new LoadOptions { Device = "gpu", ContextSize = 1024, MaxConcurrent = 1, CacheSlots = 1, Threads = 4, SkipFitCheck = true, Vision = false };
        using var rt = await LLMHostProject.Inference.ModelRuntime.LoadAsync(model, o, "monitor-gpu", null, CancellationToken.None);

        long best = 0;
        for (int i = 0; i < 6 && best < 100L * 1024 * 1024; i++)
        {
            await Task.Delay(700);
            var s = HardwareMonitor.Shared.Sample();
            best = Math.Max(best, s.Gpus.Max(g => g.ProcessDedicatedBytes ?? 0));
        }
        Assert.True(best > 100L * 1024 * 1024, $"the loaded model should show up as this process's GPU memory (saw {best} bytes)");
    }
}
