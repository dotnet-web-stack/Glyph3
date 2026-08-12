using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// QUIC variable-length integers (RFC 9000 16). The two leading bits select the width, so the
/// boundaries between 1, 2, 4 and 8 byte forms are where this goes wrong.
/// </summary>
public class VarintTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(37, 1)]
    [InlineData(63, 1)]              // largest 1-byte
    [InlineData(64, 2)]              // smallest 2-byte
    [InlineData(16383, 2)]           // largest 2-byte
    [InlineData(16384, 4)]           // smallest 4-byte
    [InlineData(1073741823, 4)]      // largest 4-byte
    [InlineData(1073741824, 8)]      // smallest 8-byte
    [InlineData(4611686018427387903, 8)]  // largest encodable
    public void RoundTripsAtEveryWidthBoundary(long value, int expectedLength)
    {
        Span<byte> buffer = stackalloc byte[8];

        int written = Varint.Write(buffer, value);
        Assert.Equal(expectedLength, written);

        Assert.True(Varint.TryRead(buffer[..written], out long read, out int consumed));
        Assert.Equal(value, read);
        Assert.Equal(written, consumed);
    }

    [Fact]
    public void MatchesTheWorkedExamplesFromTheRfc()
    {
        // RFC 9000 A.1.
        Assert.True(Varint.TryRead([0xc2, 0x19, 0x7c, 0x5e, 0xff, 0x14, 0xe8, 0x8c], out long eight, out _));
        Assert.Equal(151288809941952652, eight);

        Assert.True(Varint.TryRead([0x9d, 0x7f, 0x3e, 0x7d], out long four, out _));
        Assert.Equal(494878333, four);

        Assert.True(Varint.TryRead([0x7b, 0xbd], out long two, out _));
        Assert.Equal(15293, two);

        Assert.True(Varint.TryRead([0x25], out long one, out _));
        Assert.Equal(37, one);
    }

    [Theory]
    [InlineData(new byte[] { })]                          // nothing at all
    [InlineData(new byte[] { 0x40 })]                     // 2-byte form, 1 byte present
    [InlineData(new byte[] { 0x80, 0x00 })]               // 4-byte form, 2 bytes present
    [InlineData(new byte[] { 0xc0, 0x00, 0x00, 0x00 })]   // 8-byte form, 4 bytes present
    public void ATruncatedValueIsNotReadable(byte[] input)
    {
        Assert.False(Varint.TryRead(input, out _, out _));
    }

    [Fact]
    public void ReadsOnlyItsOwnBytesFromALongerBuffer()
    {
        // The parsers hand it the rest of the frame, so it must not consume past its own value.
        Span<byte> buffer = stackalloc byte[8];
        int written = Varint.Write(buffer, 16383);

        Assert.True(Varint.TryRead(buffer, out long value, out int consumed));
        Assert.Equal(16383, value);
        Assert.Equal(written, consumed);
        Assert.Equal(2, consumed);
    }
}
