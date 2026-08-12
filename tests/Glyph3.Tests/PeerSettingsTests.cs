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

/// <summary>
/// The capacity is a dial, and 0 has to switch the whole mechanism off rather than merely
/// discourage it.
/// </summary>
public class Http3OptionsTests
{
    [Fact]
    public void DefaultsToTheDynamicTableOff()
    {
        var options = new Http3Options();

        Assert.Equal(0, options.QpackDynamicTableCapacity);
        Assert.Equal(0, options.QpackBlockedStreams);
        Assert.False(options.DynamicTableEnabled);
    }

    [Fact]
    public void ANonZeroCapacityIsRefusedWhileTheDecoderCannotHonourIt()
    {
        // Advertising a table this build cannot decode would be worse than not offering one:
        // conforming peers would send references it refuses.
        var transport = new NullTransport();

        Assert.Throws<NotSupportedException>(() => new Http3Connection(
            transport,
            _ => new Http3Response { Status = 200 },
            new Http3Options { QpackDynamicTableCapacity = 4096 }));
    }

    [Fact]
    public void SettingsCarryTheConfiguredValues()
    {
        var transport = new RecordingTransport();

        var connection = new Http3Connection(transport, _ => new Http3Response { Status = 200 });
        connection.Start();

        // stream type 0x00, SETTINGS, length, then id/value pairs
        byte[] sent = transport.Sent;

        Assert.Equal(0x00, sent[0]);
        Assert.Equal(0x04, sent[1]);
        Assert.Equal(4, sent[2]);
        Assert.Equal(0x01, sent[3]);   // QPACK_MAX_TABLE_CAPACITY
        Assert.Equal(0x00, sent[4]);   //   = 0
        Assert.Equal(0x07, sent[5]);   // QPACK_BLOCKED_STREAMS
        Assert.Equal(0x00, sent[6]);   //   = 0
    }

    private sealed class NullTransport : IHttp3Transport
    {
        public long OpenUniStream() => 3;
        public void Send(long streamId, ReadOnlySpan<byte> data, bool fin) { }
    }

    private sealed class RecordingTransport : IHttp3Transport
    {
        internal byte[] Sent = [];

        public long OpenUniStream() => 3;

        public void Send(long streamId, ReadOnlySpan<byte> data, bool fin) => Sent = data.ToArray();
    }
}
