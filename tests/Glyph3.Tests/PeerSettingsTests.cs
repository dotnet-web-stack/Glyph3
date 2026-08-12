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
    public void ANonZeroCapacityIsAccepted()
    {
        var transport = new NullTransport();

        var connection = new Http3Connection(
            transport,
            _ => new Http3Response { Status = 200 },
            new Http3Options { QpackDynamicTableCapacity = 4096 });

        Assert.False(connection.IsFaulted);
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

/// <summary>
/// The dial, end to end: capacity 0 must behave exactly as before, and a nonzero one must actually
/// open the decoder stream and advertise itself.
/// </summary>
public class DynamicTableWiringTests
{
    [Fact]
    public void CapacityZeroOpensOnlyTheControlStream()
    {
        var transport = new CountingTransport();

        new Http3Connection(transport, _ => new Http3Response { Status = 200 }).Start();

        Assert.Equal(1, transport.UniStreamsOpened);
    }

    [Fact]
    public void ANonZeroCapacityAlsoOpensTheDecoderStream()
    {
        var transport = new CountingTransport();

        new Http3Connection(transport, _ => new Http3Response { Status = 200 },
            new Http3Options { QpackDynamicTableCapacity = 4096 }).Start();

        Assert.Equal(2, transport.UniStreamsOpened);

        // Stream type 0x03 announces it as the QPACK decoder stream.
        Assert.Equal(0x03, transport.LastSent[0]);
    }

    [Fact]
    public void TheAdvertisedCapacityReachesTheWire()
    {
        var transport = new CountingTransport();

        new Http3Connection(transport, _ => new Http3Response { Status = 200 },
            new Http3Options { QpackDynamicTableCapacity = 4096 }).Start();

        // SETTINGS: ... 0x01 then 4096 as a 2-byte varint (0x50 0x00).
        byte[] settings = transport.FirstSent;

        Assert.Equal(0x01, settings[3]);
        Assert.Equal(0x50, settings[4]);
        Assert.Equal(0x00, settings[5]);
    }

    [Fact]
    public void BlockedStreamsAboveZeroIsRefused()
    {
        // We refuse blocked references rather than parking them, so inviting the peer to send one
        // would only break connections.
        Assert.Throws<NotSupportedException>(() => new Http3Connection(
            new CountingTransport(),
            _ => new Http3Response { Status = 200 },
            new Http3Options { QpackDynamicTableCapacity = 4096, QpackBlockedStreams = 16 }));
    }

    private sealed class CountingTransport : IHttp3Transport
    {
        private long _next = -1;

        internal int UniStreamsOpened { get; private set; }

        internal byte[] FirstSent { get; private set; } = [];

        internal byte[] LastSent { get; private set; } = [];

        public long OpenUniStream()
        {
            UniStreamsOpened++;
            return _next += 4;
        }

        public void Send(long streamId, ReadOnlySpan<byte> data, bool fin)
        {
            if (FirstSent.Length == 0)
            {
                FirstSent = data.ToArray();
            }
            LastSent = data.ToArray();
        }
    }
}

/// <summary>
/// The SETTINGS frame's own framing. Its length was a constant, correct only while every value was
/// a single-byte zero, so configuring a real capacity truncated the frame and killed the
/// connection - with nothing logged, because the peer simply gave up.
/// </summary>
public class SettingsFramingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(63)]      // last single-byte varint
    [InlineData(64)]      // first two-byte varint
    [InlineData(4096)]
    [InlineData(16384)]   // first four-byte varint
    public void TheDeclaredLengthMatchesThePayload(int capacity)
    {
        var transport = new CapturingTransport();

        new Http3Connection(transport, _ => new Http3Response { Status = 200 },
            new Http3Options { QpackDynamicTableCapacity = capacity }).Start();

        byte[] sent = transport.First;

        // stream type, frame type, then the length varint.
        Assert.True(Varint.TryRead(sent.AsSpan(0), out long streamType, out int c0));
        Assert.Equal(0x00, streamType);

        Assert.True(Varint.TryRead(sent.AsSpan(c0), out long frameType, out int c1));
        Assert.Equal(0x4, frameType);

        Assert.True(Varint.TryRead(sent.AsSpan(c0 + c1), out long length, out int c2));

        int payloadStart = c0 + c1 + c2;
        Assert.Equal(sent.Length - payloadStart, length);
    }

    [Fact]
    public void TheAdvertisedCapacityIsReadableBack()
    {
        var transport = new CapturingTransport();

        new Http3Connection(transport, _ => new Http3Response { Status = 200 },
            new Http3Options { QpackDynamicTableCapacity = 4096 }).Start();

        byte[] sent = transport.First;

        // Skip stream type, frame type and length, then read the identifier/value pairs.
        Varint.TryRead(sent.AsSpan(0), out _, out int c0);
        Varint.TryRead(sent.AsSpan(c0), out _, out int c1);
        Varint.TryRead(sent.AsSpan(c0 + c1), out _, out int c2);

        ReadOnlySpan<byte> payload = sent.AsSpan(c0 + c1 + c2);

        Assert.True(Varint.TryRead(payload, out long id, out int i0));
        Assert.Equal(0x1, id);

        Assert.True(Varint.TryRead(payload[i0..], out long value, out _));
        Assert.Equal(4096, value);
    }

    private sealed class CapturingTransport : IHttp3Transport
    {
        private long _next = -1;

        internal byte[] First { get; private set; } = [];

        public long OpenUniStream() => _next += 4;

        public void Send(long streamId, ReadOnlySpan<byte> data, bool fin)
        {
            if (First.Length == 0)
            {
                First = data.ToArray();
            }
        }
    }
}
