using System.Buffers;

namespace Glyph3;

/// <summary>
/// The write half of a streamed response: the handler pushes body bytes and each flush becomes a
/// DATA frame.
/// </summary>
/// <remarks>
/// An <see cref="IBufferWriter{T}"/> because that is what serializers and response sinks already
/// write into. <see cref="FlushAsync"/> waits when the transport reports no send capacity, so a
/// producer cannot outrun a stalled peer. Single-threaded, like the rest of the connection.
/// </remarks>
public sealed class Http3ResponseWriter : IBufferWriter<byte>
{
    private const int DefaultChunk = 16 * 1024;
    private const long FrameData = 0x0;

    private readonly Http3Connection _connection;
    private readonly IHttp3Transport _transport;
    private long _streamId;

    private byte[] _staging = [];
    private int _staged;

    private bool _headersSent;
    private bool _completed;

    internal Http3ResponseWriter(Http3Connection connection, IHttp3Transport transport, long streamId)
    {
        _connection = connection;
        _transport = transport;
        _streamId = streamId;
    }

    /// <summary>The stream this response belongs to.</summary>
    public long StreamId => _streamId;

    /// <summary>True once the body has been finished and the stream closed.</summary>
    public bool IsCompleted => _completed;

    /// <summary>
    /// Send the response headers. Once, before any body byte. No content-length: a streamed
    /// response has none, and each DATA frame carries its own length.
    /// </summary>
    public void WriteHeaders(Http3Response response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_headersSent)
        {
            throw new InvalidOperationException("Response headers have already been written for this stream.");
        }
        if (!response.Body.IsEmpty)
        {
            throw new ArgumentException(
                "A streamed response carries its body through the writer; leave Response.Body empty.",
                nameof(response));
        }

        _headersSent = true;
        _connection.SendStreamedHeaders(_streamId, response);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureStaging(sizeHint <= 0 ? 1 : sizeHint);
        return _staging.AsSpan(_staged);
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureStaging(sizeHint <= 0 ? 1 : sizeHint);
        return _staging.AsMemory(_staged);
    }

    /// <inheritdoc />
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_staged + count > _staging.Length)
        {
            throw new InvalidOperationException("Advanced past the end of the span handed out by GetSpan.");
        }
        _staged += count;
    }

    /// <summary>
    /// Send what is staged as one DATA frame, waiting first if the transport has no send capacity.
    /// That wait is the backpressure, and bounds memory to one chunk.
    /// </summary>
    public ValueTask FlushAsync() => FlushCore(fin: false);

    /// <summary>
    /// Send what is left and close the stream. Called for a handler that returns without it.
    /// </summary>
    public async ValueTask CompleteAsync()
    {
        if (_completed)
        {
            return;
        }

        if (!_headersSent)
        {
            WriteHeaders(new Http3Response { Status = 500 });
        }

        await FlushCore(fin: true);
        _completed = true;

        if (_staging.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_staging);
            _staging = [];
        }
    }

    private async ValueTask FlushCore(bool fin)
    {
        if (!_headersSent)
        {
            throw new InvalidOperationException("Write the response headers before flushing a body chunk.");
        }

        if (_staged == 0 && !fin)
        {
            return;
        }

        // Wait rather than queue: unbounded queueing is what streaming exists to avoid.
        while (!_transport.CanSend && !_connection.IsBroken)
        {
            await _connection.WaitForSendCapacityAsync();
        }

        if (_connection.IsBroken)
        {
            return;
        }

        if (_staged == 0)
        {
            _transport.Send(_streamId, ReadOnlySpan<byte>.Empty, fin: true);
            return;
        }

        // [0x00][varint length][payload] in one call, to avoid a second trip through the
        // transport per chunk.
        Span<byte> header = stackalloc byte[16];
        int h = Varint.Write(header, FrameData);
        h += Varint.Write(header[h..], _staged);

        byte[] frame = ArrayPool<byte>.Shared.Rent(h + _staged);
        header[..h].CopyTo(frame);
        _staging.AsSpan(0, _staged).CopyTo(frame.AsSpan(h));

        _transport.Send(_streamId, frame.AsSpan(0, h + _staged), fin);
        ArrayPool<byte>.Shared.Return(frame);

        _staged = 0;
    }

    private void EnsureStaging(int sizeHint)
    {
        int needed = _staged + sizeHint;
        if (_staging.Length >= needed)
        {
            return;
        }

        byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(needed, DefaultChunk));
        if (_staged > 0)
        {
            _staging.AsSpan(0, _staged).CopyTo(grown);
        }
        if (_staging.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_staging);
        }
        _staging = grown;
    }

    /// <summary>Reuse this writer for another stream, keeping its staging buffer.</summary>
    internal void Reset(long streamId)
    {
        _streamId = streamId;
        _staged = 0;
        _headersSent = false;
        _completed = false;
    }
}
