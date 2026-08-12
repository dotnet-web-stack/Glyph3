using System.Text;

using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// The QPACK decoder: what a peer actually sends us. A bug here corrupts headers rather than
/// throwing, so these assert on decoded values, not just on success.
/// </summary>
public class QpackDecodeTests
{
    [Fact]
    public void DecodesIndexedStaticFields()
    {
        // :method GET is static index 17, :path / is index 1.
        var request = Decode([.. Prefix(), .. Indexed(17), .. Indexed(1)]);

        Assert.Equal("GET", Text(request.Method));
        Assert.Equal("/", Text(request.Path));
    }

    [Fact]
    public void DecodesALiteralWithAStaticNameReference()
    {
        // Literal With Name Reference: 01 N T index(4+). T=1 static. Index 15 is :method.
        var request = Decode([.. Prefix(), .. LiteralWithStaticName(15, "PATCH")]);

        Assert.Equal("PATCH", Text(request.Method));
    }

    [Fact]
    public void DecodesALiteralNameAndValue()
    {
        var request = Decode([.. Prefix(), .. LiteralNameAndValue("x-trace", "abc123")]);

        Assert.Single(request.Headers);
        Assert.Equal("x-trace", Text(request.Headers[0].Name));
        Assert.Equal("abc123", Text(request.Headers[0].Value));
    }

    [Fact]
    public void PseudoHeadersAreRoutedAndNotListedAsHeaders()
    {
        var request = Decode([.. Prefix(), .. Indexed(17), .. Indexed(1), .. LiteralNameAndValue("accept", "*/*")]);

        Assert.Equal("GET", Text(request.Method));
        Assert.Equal("/", Text(request.Path));

        // The pseudo-headers route into their own properties; only real fields land in Headers.
        Assert.Single(request.Headers);
        Assert.Equal("accept", Text(request.Headers[0].Name));
    }

    [Fact]
    public void ADynamicReferenceIsRefused()
    {
        // Indexed Field Line with T=0 means the dynamic table, which capacity 0 forbids.
        Assert.False(TryDecode([.. Prefix(), 0x81]));
    }

    [Fact]
    public void ADynamicNameReferenceIsRefused()
    {
        // Literal With Name Reference, T=0.
        Assert.False(TryDecode([.. Prefix(), 0x41, 0x01, 0x61]));
    }

    [Fact]
    public void ANonZeroRequiredInsertCountIsRefused()
    {
        // The whole contract of advertising capacity 0: a conforming peer sends RIC 0. Anything
        // else references a table we do not keep.
        Assert.False(TryDecode([0x01, 0x00, .. Indexed(17)]));
    }

    [Fact]
    public void ATruncatedFieldSectionIsRefused()
    {
        // Literal announcing 5 bytes of value with 2 present.
        Assert.False(TryDecode([.. Prefix(), 0x23, .. Ascii("x-a"), 0x05, .. Ascii("ab")]));
    }

    [Fact]
    public void AnOutOfRangeStaticIndexIsRefused()
    {
        // The static table has 99 entries; 120 is past the end.
        Assert.False(TryDecode([.. Prefix(), .. Indexed(120)]));
    }

    // --- helpers: bytes built explicitly, since a hex pair and a bit string look alike ---

    private static Http3Request Decode(byte[] section)
    {
        var request = new Http3Request();
        Assert.True(Qpack.TryDecodeFieldSection(section, request), "the field section should have decoded");
        request.Freeze();
        return request;
    }

    private static bool TryDecode(byte[] section) => Qpack.TryDecodeFieldSection(section, new Http3Request());

    /// <summary>Required Insert Count 0, then sign+Delta Base 0: the no-dynamic-table prefix.</summary>
    private static byte[] Prefix() => [0x00, 0x00];

    /// <summary>Indexed Field Line, static table: 1 T=1 index(6+).</summary>
    private static byte[] Indexed(int index)
        => index < 63 ? [(byte)(0xc0 | index)] : [0xff, .. Prefixed(index - 63)];

    /// <summary>Literal With Name Reference, static: 0 1 N=0 T=1 index(4+), then the value.</summary>
    private static byte[] LiteralWithStaticName(int index, string value)
        => index < 15
            ? [(byte)(0x50 | index), .. StringLiteral(value)]
            : [0x5f, .. Prefixed(index - 15), .. StringLiteral(value)];

    /// <summary>Literal With Literal Name: 0 0 1 N=0 H=0 namelen(3+), name, then the value.</summary>
    private static byte[] LiteralNameAndValue(string name, string value)
        => name.Length < 7
            ? [(byte)(0x20 | name.Length), .. Ascii(name), .. StringLiteral(value)]
            : [0x27, .. Prefixed(name.Length - 7), .. Ascii(name), .. StringLiteral(value)];

    /// <summary>A string: H=0 then a 7-bit-prefix length, then raw bytes.</summary>
    private static byte[] StringLiteral(string value)
        => value.Length < 127
            ? [(byte)value.Length, .. Ascii(value)]
            : [0x7f, .. Prefixed(value.Length - 127), .. Ascii(value)];

    /// <summary>The continuation bytes of a prefixed integer once the prefix is saturated.</summary>
    private static byte[] Prefixed(int remainder)
    {
        var output = new List<byte>();

        while (remainder >= 128)
        {
            output.Add((byte)((remainder & 0x7f) | 0x80));
            remainder >>= 7;
        }

        output.Add((byte)remainder);
        return [.. output];
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.ASCII.GetString(value.Span);
}
