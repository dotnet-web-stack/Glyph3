using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// The peer's SETTINGS, read off its control stream. These decide whether responses may use a
/// dynamic table, so getting them wrong is not cosmetic.
/// </summary>
public class PeerSettingsTests
{
    [Fact]
    public void ReadsQpackCapacityAndBlockedStreams()
    {
        var connection = Connect(out var transport);

        Deliver(connection, Settings((0x1, 4096), (0x7, 16)));

        Assert.Equal(4096, connection.PeerQpackCapacity());
        Assert.Equal(16, connection.PeerQpackBlockedStreams());
    }

    [Fact]
    public void UnknownIdentifiersAreIgnored()
    {
        // Greasing: peers deliberately send identifiers nobody knows, and a parser that chokes on
        // them fails against real clients.
        var connection = Connect(out var transport);

        Deliver(connection, Settings((0x1f * 3 + 0x21, 1), (0x1, 512), (0x4242, 9)));

        Assert.Equal(512, connection.PeerQpackCapacity());
    }

    [Fact]
    public void NoSettingsMeansZero()
    {
        var connection = Connect(out _);

        Assert.Equal(0, connection.PeerQpackCapacity());
        Assert.Equal(0, connection.PeerQpackBlockedStreams());
    }

    [Fact]
    public void PayloadSplitAcrossReadsIsStillRead()
    {
        // The control stream is a byte stream: a SETTINGS frame can arrive in any number of pieces.
        var connection = Connect(out var transport);

        byte[] frame = Settings((0x1, 4096), (0x7, 16));

        for (int i = 0; i < frame.Length; i++)
        {
            connection.Feed(2, frame.AsSpan(i, 1), fin: false);
        }
        connection.Flush();

        Assert.Equal(4096, connection.PeerQpackCapacity());
        Assert.Equal(16, connection.PeerQpackBlockedStreams());
    }

    // --- helpers ---

    private static void Deliver(Http3Connection connection, byte[] frame)
    {
        connection.Feed(2, frame, fin: false);
        connection.Flush();
    }

    private static Http3Connection Connect(out TestTransport transport)
    {
        transport = new TestTransport();
        var connection = new Http3Connection(transport, _ => new Http3Response { Status = 200 });
        connection.Start();
        return connection;
    }

    /// <summary>A client control stream: stream type 0x00, then a SETTINGS frame.</summary>
    private static byte[] Settings(params (long Id, long Value)[] settings)
    {
        var payload = new List<byte>();
        Span<byte> scratch = stackalloc byte[8];

        foreach ((long id, long value) in settings)
        {
            payload.AddRange(scratch[..Varint.Write(scratch, id)].ToArray());
            payload.AddRange(scratch[..Varint.Write(scratch, value)].ToArray());
        }

        var frame = new List<byte>();
        frame.AddRange(scratch[..Varint.Write(scratch, 0x00)].ToArray());          // stream type: control
        frame.AddRange(scratch[..Varint.Write(scratch, 0x4)].ToArray());           // SETTINGS
        frame.AddRange(scratch[..Varint.Write(scratch, payload.Count)].ToArray()); // length
        frame.AddRange(payload);

        return [.. frame];
    }

    private sealed class TestTransport : IHttp3Transport
    {
        private long _next = -1;

        public long OpenUniStream() => _next += 4;

        public void Send(long streamId, ReadOnlySpan<byte> data, bool fin) { }
    }
}
