using System.Text;

using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// Huffman decoding (RFC 7541 Appendix B). Peers Huffman-code almost every literal, so this runs on
/// nearly every request, and a wrong symbol yields a plausible-looking wrong header rather than an
/// error.
/// </summary>
public class HuffmanTests
{
    [Theory]
    // Encodings taken from RFC 7541 C.4 and C.6.
    [InlineData(new byte[] { 0xf1, 0xe3, 0xc2, 0xe5, 0xf2, 0x3a, 0x6b, 0xa0, 0xab, 0x90, 0xf4, 0xff }, "www.example.com")]
    [InlineData(new byte[] { 0xa8, 0xeb, 0x10, 0x64, 0x9c, 0xbf }, "no-cache")]
    [InlineData(new byte[] { 0x25, 0xa8, 0x49, 0xe9, 0x5b, 0xa9, 0x7d, 0x7f }, "custom-key")]
    [InlineData(new byte[] { 0x25, 0xa8, 0x49, 0xe9, 0x5b, 0xb8, 0xe8, 0xb4, 0xbf }, "custom-value")]
    public void DecodesTheRfcExamples(byte[] encoded, string expected)
    {
        Span<byte> output = new byte[Huffman.MaxDecodedLength(encoded.Length)];

        int written = Huffman.Decode(encoded, output);

        Assert.Equal(expected, Encoding.ASCII.GetString(output[..written]));
    }

    [Fact]
    public void AnEmptyInputDecodesToNothing()
    {
        Assert.Equal(0, Huffman.Decode([], new byte[8]));
    }

    [Fact]
    public void MaxDecodedLengthCoversTheShortestSymbol()
    {
        // The shortest code is 5 bits, so 8 bytes can carry at most 12 symbols. The bound has to be
        // at least that or the decode overflows its buffer.
        Assert.True(Huffman.MaxDecodedLength(8) >= 8 * 8 / 5);
    }

    [Fact]
    public void DecodedOutputIsIndependentOfHowTheBufferWasSized()
    {
        byte[] encoded = [0xa8, 0xeb, 0x10, 0x64, 0x9c, 0xbf];

        Span<byte> tight = new byte[Huffman.MaxDecodedLength(encoded.Length)];
        Span<byte> roomy = new byte[256];

        int a = Huffman.Decode(encoded, tight);
        int b = Huffman.Decode(encoded, roomy);

        Assert.Equal(a, b);
        Assert.Equal(tight[..a].ToArray(), roomy[..b].ToArray());
    }
}
