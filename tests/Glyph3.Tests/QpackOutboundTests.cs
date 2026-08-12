using System.Text;

using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// The encoding side of the dynamic table, proved by decoding what it produced with a table built
/// from the same instructions - which is exactly what the peer does.
/// </summary>
public class QpackOutboundTests
{
    private const int Capacity = 4096;

    [Fact]
    public void NothingIsReferencedUntilThePeerAcknowledges()
    {
        var encoder = new QpackEncoder(Capacity);

        Span<byte> instruction = stackalloc byte[128];
        Assert.True(encoder.TryInsert(instruction, "server"u8, "glyph"u8) > 0);

        // Inserted, but not yet acknowledged: referencing it could arrive before the insertion.
        Assert.Equal(-1, encoder.FindReferenceable("server"u8, "glyph"u8));

        Acknowledge(encoder, 1);

        Assert.Equal(0, encoder.FindReferenceable("server"u8, "glyph"u8));
    }

    [Fact]
    public void AHeaderAlreadyInTheTableIsNotInsertedTwice()
    {
        var encoder = new QpackEncoder(Capacity);
        Span<byte> instruction = stackalloc byte[128];

        Assert.True(encoder.TryInsert(instruction, "server"u8, "glyph"u8) > 0);
        Assert.Equal(0, encoder.TryInsert(instruction, "server"u8, "glyph"u8));
        Assert.Equal(1, encoder.InsertCount);
    }

    [Fact]
    public void NothingIsInsertedOnceTheTableIsFull()
    {
        // Append-only: when it is full it stops, so an entry can never vanish under a reference.
        var encoder = new QpackEncoder(peerCapacity: 80);
        Span<byte> instruction = stackalloc byte[128];

        Assert.True(encoder.TryInsert(instruction, "a"u8, "1"u8) > 0);
        Assert.True(encoder.TryInsert(instruction, "b"u8, "2"u8) > 0);
        Assert.Equal(0, encoder.TryInsert(instruction, "c"u8, "3"u8));
        Assert.Equal(2, encoder.InsertCount);
    }

    [Fact]
    public void AReferencedHeaderRoundTripsThroughTheDecoder()
    {
        (QpackEncoder encoder, QpackDynamicTable peerTable) = Paired(("server", "glyph"));

        var response = new Http3Response { Status = 200 };
        Add(response, "server", "glyph");

        Http3Request decoded = RoundTrip(response, encoder, peerTable);

        Assert.Equal("server", Text(decoded.Headers[0].Name));
        Assert.Equal("glyph", Text(decoded.Headers[0].Value));
    }

    [Fact]
    public void ReferencedAndLiteralHeadersMixInOneSection()
    {
        (QpackEncoder encoder, QpackDynamicTable peerTable) = Paired(("server", "glyph"));

        var response = new Http3Response { Status = 200 };
        Add(response, "server", "glyph");        // referenced
        Add(response, "x-once", "unique");       // literal, never inserted yet

        Http3Request decoded = RoundTrip(response, encoder, peerTable);

        Assert.Equal("server", Text(decoded.Headers[0].Name));
        Assert.Equal("glyph", Text(decoded.Headers[0].Value));
        Assert.Equal("x-once", Text(decoded.Headers[1].Name));
        Assert.Equal("unique", Text(decoded.Headers[1].Value));
    }

    [Fact]
    public void SeveralReferencesResolveToTheRightEntries()
    {
        // The relative indices count backwards from Base, so an off-by-one swaps headers rather
        // than failing.
        (QpackEncoder encoder, QpackDynamicTable peerTable) =
            Paired(("x-a", "1"), ("x-b", "2"), ("x-c", "3"));

        var response = new Http3Response { Status = 200 };
        Add(response, "x-c", "3");
        Add(response, "x-a", "1");
        Add(response, "x-b", "2");

        Http3Request decoded = RoundTrip(response, encoder, peerTable);

        Assert.Equal("x-c", Text(decoded.Headers[0].Name));
        Assert.Equal("x-a", Text(decoded.Headers[1].Name));
        Assert.Equal("x-b", Text(decoded.Headers[2].Name));
    }

    [Fact]
    public void WithNoEncoderTheSectionIsUnchanged()
    {
        // The capacity-0 path must produce exactly what it always did.
        var response = new Http3Response { Status = 200 };
        Add(response, "server", "glyph");

        byte[] encoded = Qpack.EncodeResponseFields(response, out int written);

        Assert.Equal(0x00, encoded[0]);   // Required Insert Count 0
        Assert.Equal(0x00, encoded[1]);   // Delta Base 0

        var request = new Http3Request();
        Assert.True(Qpack.TryDecodeFieldSection(encoded.AsSpan(0, written), request));
    }

    // --- helpers ---

    /// <summary>
    /// An encoder with the given headers inserted and acknowledged, plus the table the peer would
    /// have built from the same instructions.
    /// </summary>
    private static (QpackEncoder Encoder, QpackDynamicTable PeerTable) Paired(params (string Name, string Value)[] headers)
    {
        var encoder = new QpackEncoder(Capacity);
        var peerTable = new QpackDynamicTable(Capacity);

        Span<byte> instruction = stackalloc byte[256];

        foreach ((string name, string value) in headers)
        {
            int written = encoder.TryInsert(instruction, Ascii(name), Ascii(value));
            Assert.True(written > 0);

            // The peer applies the very instruction we emitted, which is what keeps the two tables
            // identical - and what this asserts.
            ReadOnlySpan<byte> span = instruction[..written];
            Assert.Equal(QpackEncoderStream.Result.Done, QpackEncoderStream.Apply(ref span, peerTable, Capacity));
        }

        Acknowledge(encoder, headers.Length);

        return (encoder, peerTable);
    }

    private static void Acknowledge(QpackEncoder encoder, int count)
    {
        Span<byte> ack = stackalloc byte[8];
        int written = QpackDecoderStream.WriteInsertCountIncrement(ack, count);

        ReadOnlySpan<byte> span = ack[..written];
        Assert.Equal(QpackDecoderStreamReader.Result.Done, encoder.ApplyDecoderStream(ref span));
    }

    private static Http3Request RoundTrip(Http3Response response, QpackEncoder encoder, QpackDynamicTable peerTable)
    {
        byte[] encoded = Qpack.EncodeResponseFields(response, encoder, out int written);

        var request = new Http3Request();
        Assert.True(Qpack.TryDecodeFieldSection(encoded.AsSpan(0, written), request, peerTable, Capacity),
            "the peer could not decode what the encoder produced");
        request.Freeze();

        return request;
    }

    private static void Add(Http3Response response, string name, string value)
    {
        ReadOnlyMemory<byte> n = Ascii(name);
        ReadOnlyMemory<byte> v = Ascii(value);
        response.Headers.Add((n, v));
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.ASCII.GetString(value.Span);
}
