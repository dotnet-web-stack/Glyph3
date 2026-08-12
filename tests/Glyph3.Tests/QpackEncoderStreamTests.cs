using System.Text;

using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// The peer's encoder-stream instructions, which build our decode table. Getting these wrong
/// desynchronises the two tables, and every later header then decodes to a wrong value silently.
/// </summary>
public class QpackEncoderStreamTests
{
    [Fact]
    public void InsertsWithAStaticNameReference()
    {
        var table = new QpackDynamicTable(4096);

        // 1 T=1 index(6+) = static entry 15 (:method), value "PATCH".
        Apply(table, [0xcf, .. Str("PATCH")]);

        Assert.Equal(1, table.InsertCount);
        Assert.True(table.TryGet(0, out var name, out var value));
        Assert.Equal(":method", Text(name));
        Assert.Equal("PATCH", Text(value));
    }

    [Fact]
    public void InsertsWithALiteralName()
    {
        var table = new QpackDynamicTable(4096);

        // 01 H=0 namelen(5+) = 7, "x-trace", then the value.
        Apply(table, [0x47, .. Ascii("x-trace"), .. Str("abc123")]);

        Assert.True(table.TryGet(0, out var name, out var value));
        Assert.Equal("x-trace", Text(name));
        Assert.Equal("abc123", Text(value));
    }

    [Fact]
    public void InsertsWithADynamicNameReference()
    {
        var table = new QpackDynamicTable(4096);
        Apply(table, [0x47, .. Ascii("x-trace"), .. Str("first")]);

        // 1 T=0 index(6+) = relative 0, the newest entry, so the name is reused.
        Apply(table, [0x80, .. Str("second")]);

        Assert.Equal(2, table.InsertCount);
        Assert.True(table.TryGet(1, out var name, out var value));
        Assert.Equal("x-trace", Text(name));
        Assert.Equal("second", Text(value));
    }

    [Fact]
    public void DuplicateReinsertsAnExistingEntry()
    {
        var table = new QpackDynamicTable(4096);
        Apply(table, [0x43, .. Ascii("x-a"), .. Str("1")]);

        Apply(table, [0x00]);   // 000 index(5+) = relative 0

        Assert.Equal(2, table.InsertCount);
        Assert.True(table.TryGet(1, out var name, out var value));
        Assert.Equal("x-a", Text(name));
        Assert.Equal("1", Text(value));
    }

    [Fact]
    public void SetsTheTableCapacity()
    {
        var table = new QpackDynamicTable(4096);

        Apply(table, [0x20 | 10]);   // 001 capacity(5+) = 10

        Assert.Equal(10, table.Capacity);
    }

    [Fact]
    public void ACapacityAboveWhatWeAdvertisedIsAnError()
    {
        var table = new QpackDynamicTable(100);

        // 001 with a saturated 5-bit prefix, then 4096-31 as a continuation.
        Assert.Equal(QpackEncoderStream.Result.Error, ApplyRaw(table, [0x3f, 0xe1, 0x1f], advertised: 100));
    }

    [Fact]
    public void AStaticNameOutOfRangeIsAnError()
    {
        var table = new QpackDynamicTable(4096);

        // Static index 120: past the end of the 99-entry table.
        Assert.Equal(QpackEncoderStream.Result.Error, ApplyRaw(table, [0xff, 0x39, .. Str("x")], advertised: 4096));
    }

    [Fact]
    public void ADuplicateOfSomethingAbsentIsAnError()
    {
        var table = new QpackDynamicTable(4096);

        Assert.Equal(QpackEncoderStream.Result.Error, ApplyRaw(table, [0x05], advertised: 4096));
    }

    [Fact]
    public void AnInstructionSplitAcrossReadsIsAppliedOnce()
    {
        // The encoder stream is a byte stream: an instruction can arrive one byte at a time, and a
        // partial one must leave the table untouched rather than half-applied.
        var table = new QpackDynamicTable(4096);

        byte[] instruction = [0x47, .. Ascii("x-trace"), .. Str("abc123")];
        var pending = new List<byte>();

        for (int i = 0; i < instruction.Length - 1; i++)
        {
            pending.Add(instruction[i]);

            ReadOnlySpan<byte> span = pending.ToArray();
            Assert.Equal(QpackEncoderStream.Result.NeedMore, QpackEncoderStream.Apply(ref span, table, 4096));
            Assert.Equal(0, table.InsertCount);
        }

        pending.Add(instruction[^1]);

        ReadOnlySpan<byte> complete = pending.ToArray();
        Assert.Equal(QpackEncoderStream.Result.Done, QpackEncoderStream.Apply(ref complete, table, 4096));
        Assert.Equal(1, table.InsertCount);
    }

    [Fact]
    public void SeveralInstructionsInOneReadAllApply()
    {
        var table = new QpackDynamicTable(4096);

        Apply(table,
        [
            0x43, .. Ascii("x-a"), .. Str("1"),
            0x43, .. Ascii("x-b"), .. Str("2"),
            0x43, .. Ascii("x-c"), .. Str("3"),
        ]);

        Assert.Equal(3, table.InsertCount);
    }

    private static void Apply(QpackDynamicTable table, byte[] instructions)
        => Assert.Equal(QpackEncoderStream.Result.Done, ApplyRaw(table, instructions, advertised: 4096));

    private static QpackEncoderStream.Result ApplyRaw(QpackDynamicTable table, byte[] instructions, int advertised)
    {
        ReadOnlySpan<byte> span = instructions;
        return QpackEncoderStream.Apply(ref span, table, advertised);
    }

    /// <summary>A string literal: H=0 then a 7-bit-prefix length, then the bytes.</summary>
    private static byte[] Str(string value) => [(byte)value.Length, .. Ascii(value)];

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static string Text(ReadOnlySpan<byte> value) => Encoding.ASCII.GetString(value);
}
