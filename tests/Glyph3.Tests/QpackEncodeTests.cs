using System.Text;

using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// The response encoder, checked by decoding what it produces. A round trip catches the errors that
/// matter - an index off by one, a length that disagrees with its payload - which asserting on
/// bytes alone would not.
/// </summary>
public class QpackEncodeTests
{
    [Theory]
    [InlineData(200, 25)]
    [InlineData(304, 26)]
    [InlineData(404, 27)]
    [InlineData(503, 28)]
    [InlineData(204, 64)]
    [InlineData(500, 71)]
    public void AStatusInTheStaticTableIsEncodedAsAnIndex(int status, int index)
    {
        // 1 T=1 index(6+): a whole response status in one or two bytes.
        byte[] encoded = Qpack.EncodeResponseFields(new Http3Response { Status = status }, out int written);

        byte[] expected = index < 63 ? [(byte)(0xc0 | index)] : [0xff, (byte)(index - 63)];

        Assert.Equal(expected, encoded.AsSpan(2, expected.Length).ToArray());
        Assert.True(written >= 2 + expected.Length);
    }

    [Theory]
    [InlineData(418)]
    [InlineData(599)]
    [InlineData(207)]
    public void AStatusOutsideTheStaticTableIsWrittenOut(int status)
    {
        // Literal with a name reference to :status, so the digits appear verbatim.
        byte[] encoded = Qpack.EncodeResponseFields(new Http3Response { Status = status }, out int written);

        string wire = Encoding.ASCII.GetString(encoded, 0, written);

        Assert.Contains(status.ToString(), wire);
    }

    [Fact]
    public void HeadersSurviveARoundTrip()
    {
        var response = new Http3Response { Status = 200 };
        Add(response, "content-type", "text/plain");
        Add(response, "x-trace", "abc123");

        Decoded decoded = RoundTrip(response);

        Assert.Equal("text/plain", decoded.Header("content-type"));
        Assert.Equal("abc123", decoded.Header("x-trace"));
    }

    [Fact]
    public void ALongHeaderValueSurvivesARoundTrip()
    {
        // Past the 7-bit prefix, so the length spills into continuation bytes.
        string value = new('v', 500);

        var response = new Http3Response { Status = 200 };
        Add(response, "x-long", value);

        Assert.Equal(value, RoundTrip(response).Header("x-long"));
    }

    [Fact]
    public void AnEmptyValueSurvivesARoundTrip()
    {
        var response = new Http3Response { Status = 200 };
        Add(response, "x-empty", "");

        Assert.Equal("", RoundTrip(response).Header("x-empty"));
    }

    [Fact]
    public void TheSectionStartsWithTheNoDynamicTablePrefix()
    {
        // Required Insert Count 0 and Delta Base 0: the constant that tells a peer this section
        // references no dynamic table.
        byte[] encoded = Qpack.EncodeResponseFields(new Http3Response { Status = 200 }, out int written);

        Assert.True(written >= 2);
        Assert.Equal(0x00, encoded[0]);
        Assert.Equal(0x00, encoded[1]);
    }

    private static void Add(Http3Response response, string name, string value)
    {
        ReadOnlyMemory<byte> n = Encoding.ASCII.GetBytes(name);
        ReadOnlyMemory<byte> v = Encoding.ASCII.GetBytes(value);
        response.Headers.Add((n, v));
    }

    // Decoded with the REQUEST decoder, which keeps ordinary headers and drops pseudo-headers it
    // does not route - :status among them. So this proves headers round trip; the status is
    // asserted against the wire above instead.
    private static Decoded RoundTrip(Http3Response response)
    {
        byte[] encoded = Qpack.EncodeResponseFields(response, out int written);

        var request = new Http3Request();
        Assert.True(Qpack.TryDecodeFieldSection(encoded.AsSpan(0, written), request), "the encoder produced something undecodable");
        request.Freeze();

        return new Decoded(request);
    }

    private sealed class Decoded
    {
        private readonly Http3Request _request;

        internal Decoded(Http3Request request) => _request = request;

        internal string Header(string name)
        {
            foreach ((ReadOnlyMemory<byte> n, ReadOnlyMemory<byte> v) in _request.Headers)
            {
                if (Encoding.ASCII.GetString(n.Span) == name)
                {
                    return Encoding.ASCII.GetString(v.Span);
                }
            }

            return "(absent)";
        }
    }
}
