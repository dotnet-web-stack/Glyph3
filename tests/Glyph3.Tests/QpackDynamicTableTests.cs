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
