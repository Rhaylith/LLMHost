using LLMHostProject.Core;
using Xunit;

namespace LLMHostProject.Tests;

[Trait("Category", "Unit")]
public class GpuDedupeTests
{
    private const long Gb = 1L << 30;
    private static (string, uint, long, long, long) A(string name, long luid) => (name, 0x1002u, 24 * Gb, 400 * Gb, luid);
    private static Dictionary<string, int> Pci(params (string, int)[] n) => n.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void ACardListedTwiceByDxgiIsShownOnce()
    {
        var list = new List<(string Name, uint VendorId, long Vram, long Shared, long Luid)> { A("RX 7900 XTX", 1), A("RX 6900 XT", 2), A("RX 7900 XTX", 3) };
        var r = Hardware.DedupeAdapters(list, Pci(("RX 7900 XTX", 1), ("RX 6900 XT", 1)), null);
        Assert.Equal(new[] { "RX 7900 XTX", "RX 6900 XT" }, r.Select(a => a.Name));
        Assert.Equal(new long[] { 1, 2 }, r.Select(a => a.Luid));              // without counters the first one is kept
    }

    [Fact]
    public void OfTheDuplicatesTheOneWithPerformanceCountersIsKept()
    {
        var list = new List<(string Name, uint VendorId, long Vram, long Shared, long Luid)> { A("RX 7900 XTX", 1), A("RX 6900 XT", 2), A("RX 7900 XTX", 3) };
        var r = Hardware.DedupeAdapters(list, Pci(("RX 7900 XTX", 1), ("RX 6900 XT", 1)), new HashSet<long> { 2, 3 });
        Assert.Equal(new long[] { 2, 3 }, r.Select(a => a.Luid));
        Assert.Equal(new[] { "RX 6900 XT", "RX 7900 XTX" }, r.Select(a => a.Name));
    }

    [Fact]
    public void TwoIdenticalCardsStayTwoWhenWindowsHasTwoOfThem()
    {
        var list = new List<(string Name, uint VendorId, long Vram, long Shared, long Luid)> { A("RX 7900 XTX", 1), A("RX 7900 XTX", 3) };
        Assert.Equal(2, Hardware.DedupeAdapters(list, Pci(("RX 7900 XTX", 2)), null).Count);
    }

    [Fact]
    public void NamesWindowsDoesNotListAreNotTouched()
    {
        var list = new List<(string Name, uint VendorId, long Vram, long Shared, long Luid)> { A("RX 7900 XTX", 1), A("RX 7900 XTX", 3) };
        Assert.Equal(2, Hardware.DedupeAdapters(list, Pci(), null).Count);
        Assert.Equal(2, Hardware.DedupeAdapters(list, Pci(("Other GPU", 1)), null).Count);
    }
}
