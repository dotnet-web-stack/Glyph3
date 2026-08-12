using System.Text;

using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// Field sections that reference the dynamic table. The Required Insert Count is sent wrapped and
/// the indices are relative to Base, so this is the arithmetic that silently yields the wrong
/// header if it is off by one.
/// </summary>
public class QpackDynamicDecodeTests
{
    private const int Capacity = 4096;

    [Fact]
    public void ResolvesAnIndexedDynamicReference()
    {
        QpackDynamicTable table = TableWith(("x-trace", "abc123"));

        // RIC 1, Base 1 (delta 0, positive). Then 1 T=0 index(6+) = 0, relative to Base.
        var request = Decode(table, [Encoded(1), 0x00, 0x80]);

        Assert.Single(request.Headers);
        Assert.Equal("x-trace", Text(request.Headers[0].Name));
        Assert.Equal("abc123", Text(request.Headers[0].Value));
    }

    [Fact]
    public void ResolvesTheOlderOfTwoEntries()
    {
        QpackDynamicTable table = TableWith(("x-a", "1"), ("x-b", "2"));

        // Base 2. Relative 1 counts backwards, so it is absolute 0 - the older entry.
        var request = Decode(table, [Encoded(2), 0x00, 0x81]);

        Assert.Equal("x-a", Text(request.Headers[0].Name));
    }

    [Fact]
    public void ResolvesALiteralWithADynamicNameReference()
    {
        QpackDynamicTable table = TableWith(("x-trace", "first"));

        // 01 N=0 T=0 index(4+) = 0, then the value.
        var request = Decode(table, [Encoded(1), 0x00, 0x40, .. Str("second")]);

        Assert.Equal("x-trace", Text(request.Headers[0].Name));
        Assert.Equal("second", Text(request.Headers[0].Value));
    }

    [Fact]
    public void ResolvesAPostBaseReference()
    {
        QpackDynamicTable table = TableWith(("x-a", "1"), ("x-b", "2"));

        // RIC 2 with a NEGATIVE delta of 1: Base = 2 - 1 - 1 = 0. Post-base 1 is then absolute 1.
        var request = Decode(table, [Encoded(2), 0x81, 0x11]);

        Assert.Equal("x-b", Text(request.Headers[0].Name));
    }

    [Fact]
    public void RefusesAReferenceToAnInsertionWeHaveNotSeen()
    {
        // The whole point of advertising 0 blocked streams: a conforming peer never sends this, so
        // it is an error rather than something to park and wait on.
        QpackDynamicTable table = TableWith(("x-a", "1"));

        Assert.False(TryDecode(table, [Encoded(2), 0x00, 0x80]));
    }

    [Fact]
    public void RefusesAReferenceToAnEvictedEntry()
    {
        var table = new QpackDynamicTable(80);           // room for two
        table.InsertEvicting("x-a"u8, "1"u8);            // 0
        table.InsertEvicting("x-b"u8, "2"u8);            // 1
        table.InsertEvicting("x-c"u8, "3"u8);            // 2, evicts 0

        // Base 3, relative 2 = absolute 0, which is gone.
        Assert.False(TryDecode(table, [Encoded(3), 0x00, 0x82]));
    }

    [Fact]
    public void RefusesAnyDynamicReferenceWhenNoTableIsConfigured()
    {
        // Capacity 0 stays exactly as it was: the peer was told we keep nothing.
        var request = new Http3Request();

        Assert.False(Qpack.TryDecodeFieldSection([0x01, 0x00, 0x80], request));
    }

    [Fact]
    public void StaticReferencesStillWorkAlongsideDynamicOnes()
    {
        QpackDynamicTable table = TableWith(("x-trace", "abc"));

        // :method GET from the static table, then the dynamic entry.
        var request = Decode(table, [Encoded(1), 0x00, 0xd1, 0x80]);

        Assert.Equal("GET", Text(request.Method));
        Assert.Equal("x-trace", Text(request.Headers[0].Name));
    }

    // --- helpers ---

    private static QpackDynamicTable TableWith(params (string Name, string Value)[] entries)
    {
        var table = new QpackDynamicTable(Capacity);

        foreach ((string name, string value) in entries)
        {
            table.InsertEvicting(Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value));
        }

        return table;
    }

    /// <summary>
    /// The Required Insert Count as it goes on the wire: wrapped modulo twice the entry capacity,
    /// plus one (RFC 9204 4.5.1.1).
    /// </summary>
    private static byte Encoded(int requiredInsertCount)
    {
        if (requiredInsertCount == 0)
        {
            return 0;
        }

        int maxEntries = Capacity / 32;
        return (byte)(requiredInsertCount % (2 * maxEntries) + 1);
    }

    private static Http3Request Decode(QpackDynamicTable table, byte[] section)
    {
        var request = new Http3Request();
        Assert.True(Qpack.TryDecodeFieldSection(section, request, table, Capacity), "the section should have decoded");
        request.Freeze();
        return request;
    }

    private static bool TryDecode(QpackDynamicTable table, byte[] section)
        => Qpack.TryDecodeFieldSection(section, new Http3Request(), table, Capacity);

    private static byte[] Str(string value) => [(byte)value.Length, .. Encoding.ASCII.GetBytes(value)];

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.ASCII.GetString(value.Span);
}
