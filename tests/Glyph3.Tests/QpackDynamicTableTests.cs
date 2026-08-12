using Xunit;

namespace Glyph3.Tests;

public class QpackDynamicTableTests
{
    [Fact]
    public void EntryCostsNameValueAndThirtyTwo()
    {
        // RFC 9204 3.2.1. The 32 is a fixed per-entry overhead, so small headers are not free.
        Assert.Equal(32 + 6 + 5, QpackDynamicTable.EntrySize("server"u8, "glyph"u8));
    }

    [Fact]
    public void InsertReturnsAscendingAbsoluteIndices()
    {
        var table = new QpackDynamicTable(4096);

        Assert.Equal(0, table.Insert("server"u8, "glyph"u8));
        Assert.Equal(1, table.Insert("cache-control"u8, "no-cache"u8));
        Assert.Equal(2, table.InsertCount);
    }

    [Fact]
    public void FindsAnExactMatchAndAName()
    {
        var table = new QpackDynamicTable(4096);
        table.Insert("server"u8, "glyph"u8);
        table.Insert("x-trace"u8, "abc"u8);

        Assert.Equal(0, table.FindExact("server"u8, "glyph"u8));
        Assert.Equal(-1, table.FindExact("server"u8, "other"u8));

        Assert.Equal(1, table.FindName("x-trace"u8));
        Assert.Equal(-1, table.FindName("x-missing"u8));
    }

    [Fact]
    public void StopsInsertingWhenFullRatherThanEvicting()
    {
        // Append-only is the whole simplification: nothing is ever dropped, so nothing the peer
        // still references can disappear underneath it.
        var table = new QpackDynamicTable(capacity: 80);

        Assert.Equal(0, table.Insert("a"u8, "1"u8));    // 34
        Assert.Equal(1, table.Insert("b"u8, "2"u8));    // 68
        Assert.False(table.CanInsert("c"u8, "3"u8));    // would be 102
        Assert.Equal(-1, table.Insert("c"u8, "3"u8));

        Assert.Equal(2, table.InsertCount);
        Assert.Equal(68, table.Size);

        // And the earlier entries are still addressable.
        Assert.Equal(0, table.FindExact("a"u8, "1"u8));
    }

    [Fact]
    public void ACapacityOfZeroAcceptsNothing()
    {
        var table = new QpackDynamicTable(0);

        Assert.False(table.CanInsert("a"u8, "1"u8));
        Assert.Equal(0, table.InsertCount);
    }
}

/// <summary>
/// The decoding side, which must evict exactly when the peer does. If the two tables disagree about
/// what an absolute index means, every later header decodes to the wrong value with no error.
/// </summary>
public class QpackDecoderTableTests
{
    [Fact]
    public void EvictsTheOldestToMakeRoom()
    {
        var table = new QpackDynamicTable(capacity: 80);   // fits two 34-byte entries

        Assert.Equal(0, table.InsertEvicting("a"u8, "1"u8));
        Assert.Equal(1, table.InsertEvicting("b"u8, "2"u8));
        Assert.Equal(2, table.InsertEvicting("c"u8, "3"u8));   // evicts "a"

        Assert.Equal(3, table.InsertCount);
        Assert.Equal(2, table.Count);
        Assert.Equal(1, table.OldestAbsoluteIndex);
    }

    [Fact]
    public void AbsoluteIndicesKeepTheirMeaningAcrossEviction()
    {
        var table = new QpackDynamicTable(capacity: 80);
        table.InsertEvicting("a"u8, "1"u8);   // 0
        table.InsertEvicting("b"u8, "2"u8);   // 1
        table.InsertEvicting("c"u8, "3"u8);   // 2, evicts 0

        Assert.False(table.TryGet(0, out _, out _));           // gone, not renumbered

        Assert.True(table.TryGet(1, out var name, out var value));
        Assert.Equal("b"u8.ToArray(), name.ToArray());
        Assert.Equal("2"u8.ToArray(), value.ToArray());

        Assert.True(table.TryGet(2, out name, out _));
        Assert.Equal("c"u8.ToArray(), name.ToArray());

        Assert.False(table.TryGet(3, out _, out _));           // not inserted yet
    }

    [Fact]
    public void AnEntryLargerThanTheTableEmptiesItAndStoresNothing()
    {
        // RFC 9204 3.2.2. The insert count still advances, so indices stay in step with the peer.
        var table = new QpackDynamicTable(capacity: 80);
        table.InsertEvicting("a"u8, "1"u8);

        Assert.Equal(-1, table.InsertEvicting("big"u8, new string('x', 100).Select(c => (byte)c).ToArray()));

        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.Size);
        Assert.Equal(2, table.InsertCount);
    }

    [Fact]
    public void CapacityMayBeLoweredAndEvictsWhatNoLongerFits()
    {
        var table = new QpackDynamicTable(capacity: 200);
        table.InsertEvicting("a"u8, "1"u8);
        table.InsertEvicting("b"u8, "2"u8);
        table.InsertEvicting("c"u8, "3"u8);

        Assert.True(table.TrySetCapacity(40, advertised: 200));

        Assert.Equal(1, table.Count);
        Assert.Equal(2, table.OldestAbsoluteIndex);   // only the newest survived
    }

    [Fact]
    public void CapacityAboveWhatWeAdvertisedIsRefused()
    {
        // The peer may not help itself to more of our memory than we offered.
        var table = new QpackDynamicTable(capacity: 100);

        Assert.False(table.TrySetCapacity(4096, advertised: 100));
        Assert.Equal(100, table.Capacity);
    }
}
