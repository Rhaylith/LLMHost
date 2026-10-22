using System.Runtime.InteropServices;
using LLMHostProject.Core;
using LLMHostProject.Inference;
using LLMHostProject.Models;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class CpuTopologyTests
{
    // 2 groups x 4 logical processors, 2 hyper-threads per core, node = group
    private static CpuTopology Synthetic() => new(Enumerable.Range(0, 8).Select(i =>
        new LogicalCpu(i, (uint)(256 + i), i / 4, i % 4, (i % 4) / 2, i / 4, 0)).ToList());

    [Fact]
    public void TheTopologyCountsPhysicalCoresGroupsAndNodes()
    {
        var t = Synthetic();
        Assert.Equal(8, t.LogicalCount);
        Assert.Equal(4, t.PhysicalCores);
        Assert.Equal(2, t.Groups);
        Assert.Equal(2, t.Nodes.Count);
        Assert.Equal(new[] { 4, 5, 6, 7 }, t.Nodes[1].Cpus);
    }

    [Fact]
    public void WindowsRecordsAreDecoded()
    {
        const int size = 32;
        var buf = Marshal.AllocHGlobal(size * 3);
        try
        {
            for (int i = 0; i < 3; i++)
            {
                int o = i * size;
                for (int b = 0; b < size; b++) Marshal.WriteByte(buf, o + b, 0);
                Marshal.WriteInt32(buf, o, size);
                Marshal.WriteInt32(buf, o + 4, 0);
                Marshal.WriteInt32(buf, o + 8, 100 + i);
                Marshal.WriteInt16(buf, o + 12, (short)(i / 2));
                Marshal.WriteByte(buf, o + 14, (byte)(i % 2));
                Marshal.WriteByte(buf, o + 15, (byte)(i % 2));
                Marshal.WriteByte(buf, o + 17, (byte)(i / 2));
            }
            var t = CpuTopology.Parse(buf, size * 3)!;
            Assert.Equal(3, t.LogicalCount);
            Assert.Equal(new uint[] { 100, 101, 102 }, t.Cpus.Select(c => c.SetId));
            Assert.Equal(new[] { 0, 0, 1 }, t.Cpus.Select(c => c.Group));
            Assert.Equal(2, t.Groups);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [Fact]
    public void ThisMachineHasAUsableTopology()
    {
        var t = CpuTopology.Detect();
        Assert.True(t.LogicalCount >= 1 && t.PhysicalCores >= 1 && t.PhysicalCores <= t.LogicalCount);
        Assert.Equal(t.PhysicalCores, Hardware.DefaultThreads);
        Assert.Equal(t.LogicalCount, Hardware.DefaultBatchThreads);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("all", null)]
    [InlineData("0-3", "0-3")]
    [InlineData("0-1,3,5-6", "0-1,3,5-6")]
    [InlineData(" 6 , 2 ", "2,6")]
    [InlineData("node:1", "4-7")]
    [InlineData("physical", "0,2,4,6")]
    [InlineData("node:0,7", "0-3,7")]
    public void AffinityTextIsUnderstood(string? spec, string? expected)
    {
        var r = CpuAffinity.Parse(spec, Synthetic());
        if (expected == null) Assert.Null(r);
        else Assert.Equal(expected, CpuAffinity.Format(r!.Select(c => c.Global)));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("3-1")]
    [InlineData("abc")]
    [InlineData("node:5")]
    [InlineData("1-2-3")]
    public void BadAffinityTextIsRefused(string spec) => Assert.Throws<ArgumentException>(() => CpuAffinity.Parse(spec, Synthetic()));

    [Fact]
    public void FormatCollapsesRuns() => Assert.Equal("0-3,8,10-11", CpuAffinity.Format(new[] { 11, 0, 1, 2, 3, 8, 10, 1 }));

    [Theory]
    [InlineData(null, 0)]
    [InlineData("normal", 0)]
    [InlineData("HIGH", 1)]
    [InlineData("low", -1)]
    public void PrioritiesMapToWindowsOffsets(string? name, int offset) => Assert.Equal(offset, NativeThreadPolicy.PriorityOffset(name));

    [Fact]
    public void TheThreadCountFollowsTheAffinity()
    {
        Assert.Equal(3, ModelRuntime.EffectiveThreads(new LoadOptions { Threads = 3 }));
        Assert.Equal(Hardware.DefaultThreads, ModelRuntime.EffectiveThreads(new LoadOptions()));
        if (CpuTopology.Detect().LogicalCount >= 2)
            Assert.Equal(2, ModelRuntime.EffectiveThreads(new LoadOptions { CpuAffinity = "0-1" }));
    }

    [Fact]
    public void ValidationChecksAffinityAndPriority()
    {
        var ok = new AppSettings();
        ok.DefaultLoad.CpuAffinity = "0"; ok.DefaultLoad.ThreadPriority = "high";
        Assert.Empty(SettingsValidator.Validate(ok));

        var bad = new AppSettings();
        bad.DefaultLoad.CpuAffinity = "999999"; bad.DefaultLoad.ThreadPriority = "turbo";
        var fields = SettingsValidator.Validate(bad).Select(e => e.Field).ToList();
        Assert.Contains("defaultLoad.cpuAffinity", fields);
        Assert.Contains("defaultLoad.threadPriority", fields);
    }
}

[Trait("Category", "Integration")]
[Collection("Models")]
public class CpuThreadIntegrationTests
{
    [VlmFact]
    public async Task EngineThreadsArePinnedToTheChosenProcessorsAndReleasedOnUnload()
    {
        NativeBackend.Initialize();
        var topo = CpuTopology.Detect();
        var model = new ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!)!;
        var o = new LoadOptions { Device = "cpu", ContextSize = 512, MaxConcurrent = 1, CacheSlots = 1, Threads = 2, SkipFitCheck = true, Vision = false, CpuAffinity = "0-1", ThreadPriority = "normal" };
        uint[] wanted = topo.Cpus.Take(2).Select(c => c.SetId).OrderBy(x => x).ToArray();
        if (topo.LogicalCount < 2) return;

        using (var rt = await ModelRuntime.LoadAsync(model, o, "pin", null, CancellationToken.None))
        {
            await rt.CompleteAsync("Say hello.", new GenerationOptions { MaxTokens = 8, Temperature = 0 }, new GenerationStats(), CancellationToken.None);
            for (int i = 0; i < 40 && NativeThreadPolicy.FindNativeThreads().Count == 0; i++) await Task.Delay(100);
            NativeThreadPolicy.ScanNow();
            var threads = NativeThreadPolicy.FindNativeThreads();
            Assert.NotEmpty(threads);
            Assert.All(threads, t => Assert.Equal(wanted, NativeThreadPolicy.SelectedCpuSets(t).OrderBy(x => x).ToArray()));
        }
        // after the unload the threads are free again
        Assert.All(NativeThreadPolicy.FindNativeThreads(), t => Assert.Empty(NativeThreadPolicy.SelectedCpuSets(t)));
    }

    [VlmFact]
    public async Task TheThreadTestReportsEveryCount()
    {
        NativeBackend.Initialize();
        var model = new ModelLibrary().Find(Environment.GetEnvironmentVariable("LLMHOST_TEST_VLM")!)!;
        var o = new LoadOptions { Device = "cpu", ContextSize = 1024, MaxConcurrent = 1, CacheSlots = 1, Threads = 2, SkipFitCheck = true, Vision = false };
        using var rt = await ModelRuntime.LoadAsync(model, o, "bench-threads", null, CancellationToken.None);
        var rows = await rt.BenchmarkThreadsAsync(new[] { 1, 2 }, 16);
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Threads));
        Assert.All(rows, r => Assert.True(r.TokensPerSecond > 0));
    }
}
