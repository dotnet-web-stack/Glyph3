using System.Text;

using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// Response headers encoded against the static table. Checked by decoding what comes out, so an
/// index that is off by one fails here rather than on a peer.
/// </summary>
public class QpackStaticEncodeTests
{
    [Theory]
    [InlineData("content-type", "text/plain", 53)]
    [InlineData("content-type", "text/html; charset=utf-8", 52)]
    [InlineData("content-type", "application/json", 46)]
    [InlineData("accept-ranges", "bytes", 32)]
    [InlineData("cache-control", "no-cache", 39)]
    [InlineData("content-encoding", "gzip", 43)]
    [InlineData("vary", "origin", 60)]
    public void ANameAndValueBothInTheTableCostOneByte(string name, string value, int index)
    {
        byte[] encoded = Encode(name, value, out int written);

        // Prefix is two bytes, then :status, then this header as 1 T=1 index(6+).
        Assert.Equal((byte)(0xc0 | index), encoded[written - 1]);
    }

    [Theory]
    [InlineData("date", "Wed, 13 Aug 2026 22:45:39 GMT")]
    [InlineData("location", "/elsewhere")]
    [InlineData("etag", "\"abc123\"")]
    [InlineData("last-modified", "Wed, 13 Aug 2026 00:00:00 GMT")]
    [InlineData("server", "GenHTTP/11.0.0.0")]
    [InlineData("set-cookie", "a=b")]
    [InlineData("content-type", "application/x-custom")]
    public void AKnownNameWithAnUnknownValueReferencesTheNameOnly(string name, string value)
    {
        byte[] encoded = Encode(name, value, out int written);

        // The name must not appear on the wire at all - that is the entire point.
        Assert.DoesNotContain(name, Encoding.ASCII.GetString(encoded, 0, written));
        Assert.Contains(value, Encoding.ASCII.GetString(encoded, 0, written));

        AssertRoundTrips(encoded, written, name, value);
    }

    [Theory]
    [InlineData("Content-Type", "text/plain")]
    [InlineData("DATE", "Wed, 13 Aug 2026 22:45:39 GMT")]
    [InlineData("Cache-Control", "no-cache")]
    public void ACapitalisedNameStillResolves(string name, string value)
    {
        // Callers hold HTTP's conventional capitalisation; matching case-insensitively means the
        // name is never written, so there is nothing to lowercase.
        byte[] encoded = Encode(name, value, out int written);

        Assert.DoesNotContain(name.ToLowerInvariant(), Encoding.ASCII.GetString(encoded, 0, written));

        AssertRoundTrips(encoded, written, name.ToLowerInvariant(), value);
    }

    [Theory]
    [InlineData("x-custom-header", "value")]
    [InlineData("x-request-id", "abc")]
    public void AnUnknownNameIsStillWrittenOut(string name, string value)
    {
        byte[] encoded = Encode(name, value, out int written);

        Assert.Contains(name, Encoding.ASCII.GetString(encoded, 0, written));

        AssertRoundTrips(encoded, written, name, value);
    }

    [Fact]
    public void AValueIsMatchedCaseSensitively()
    {
        // Field values are case-sensitive, so TEXT/PLAIN is not entry 53 and must be written out.
        byte[] encoded = Encode("content-type", "TEXT/PLAIN", out int written);

        Assert.Contains("TEXT/PLAIN", Encoding.ASCII.GetString(encoded, 0, written));

        AssertRoundTrips(encoded, written, "content-type", "TEXT/PLAIN");
    }

    [Fact]
    public void TheStaticTableShrinksATypicalResponse()
    {
        var response = new Http3Response { Status = 200 };
        Add(response, "content-type", "text/plain");
        Add(response, "accept-ranges", "bytes");
        Add(response, "vary", "origin");

        byte[] encoded = Qpack.EncodeResponseFields(response, out int written);

        // Two prefix bytes, an indexed :status, and one byte per header.
        Assert.Equal(6, written);

        Assert.True(encoded.Length >= written);
    }

    private static byte[] Encode(string name, string value, out int written)
    {
        var response = new Http3Response { Status = 200 };

        Add(response, name, value);

        return Qpack.EncodeResponseFields(response, out written);
    }

    private static void Add(Http3Response response, string name, string value)
        => response.Headers.Add((Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value)));

    private static void AssertRoundTrips(byte[] encoded, int written, string name, string value)
    {
        var request = new Http3Request();

        Assert.True(Qpack.TryDecodeFieldSection(encoded.AsSpan(0, written), request));

        // Decoded fields are ranges into an arena until this materialises them.
        request.Freeze();

        (ReadOnlyMemory<byte> Name, ReadOnlyMemory<byte> Value) field =
            Assert.Single(request.Headers, h => Encoding.ASCII.GetString(h.Name.Span) == name);

        Assert.Equal(value, Encoding.ASCII.GetString(field.Value.Span));
    }
}
