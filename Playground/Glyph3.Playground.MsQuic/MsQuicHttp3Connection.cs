using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Quic;
using System.Threading.Channels;
using Glyph3;

namespace Glyph3.Playground.MsQuic;

/// <summary>
/// One HTTP/3 connection: MsQuic underneath, Glyph3 on top. Reads QUIC streams into
/// <see cref="Http3Connection.Feed"/> and writes back what comes out.
/// </summary>
/// <remarks>
/// Three mismatches any transport bridge hits, and how they are handled here:
/// Glyph3 is single-threaded while MsQuic reads streams concurrently, so everything funnels
/// through one channel and one consumer. <c>Send</c> is synchronous and borrows its span, so each
/// write is copied and queued for a single writer task. <c>OpenUniStream</c> is synchronous, so
/// the unidirectional streams are opened before the connection starts.
/// </remarks>
internal sealed class MsQuicHttp3Connection : IHttp3Transport, IAsyncDisposable
{
    private readonly QuicConnection _quic;
    private readonly ConcurrentDictionary<long, QuicStream> _streams = new();
    private readonly Queue<QuicStream> _spareUniStreams = new();

    // Ingress: every stream loop posts here, one consumer drains, so Glyph3 sees one thread.
    private readonly Channel<Inbound> _ingress =
        Channel.CreateUnbounded<Inbound>(new UnboundedChannelOptions { SingleReader = true });

    // Egress: Glyph3's synchronous Send lands here, one writer drains it.
    private readonly Channel<Outbound> _egress =
        Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });

    private Http3Connection? _h3;

    private readonly record struct Inbound(long StreamId, byte[]? Buffer, int Length, bool Fin, bool Closed);
    private readonly record struct Outbound(long StreamId, byte[] Buffer, int Length, bool Fin);

    private MsQuicHttp3Connection(QuicConnection quic) => _quic = quic;

    /// <summary>Serve one accepted QUIC connection until it closes.</summary>
    public static async Task ServeAsync(
        QuicConnection quic,
        Func<Http3Request, Http3Response> handler,
        CancellationToken cancellationToken)
    {
        await using var bridge = new MsQuicHttp3Connection(quic);
        await bridge.RunAsync(handler, cancellationToken);
    }

    private async Task RunAsync(Func<Http3Request, Http3Response> handler, CancellationToken cancellationToken)
    {
        // Opened before Glyph3 exists, because OpenUniStream answers synchronously. Three is what
        // HTTP/3 defines; an unwritten stream is never announced, so spares cost nothing.
        for (int i = 0; i < 3; i++)
        {
            QuicStream uni = await _quic.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken);
            _streams[uni.Id] = uni;
            _spareUniStreams.Enqueue(uni);
        }

        _h3 = new Http3Connection(this, handler);

        Task accepting = AcceptStreamsAsync(cancellationToken);
        Task writing = WriteLoopAsync(cancellationToken);

        try
        {
            await PumpAsync(cancellationToken);
        }
        finally
        {
            _h3.Close();
            _egress.Writer.TryComplete();
            _ingress.Writer.TryComplete();
            await Task.WhenAny(Task.WhenAll(accepting, writing), Task.Delay(1000, CancellationToken.None));
        }
    }

    // The one place Glyph3 is called, so it never needs a lock.
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        _h3!.Start();

        while (await _ingress.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_ingress.Reader.TryRead(out Inbound item))
            {
                if (item.Closed)
                {
                    _h3.OnStreamClosed(item.StreamId);
                }
                else
                {
                    _h3.Feed(item.StreamId, item.Buffer.AsSpan(0, item.Length), item.Fin);

                    // The end-of-stream marker carries no buffer, only fin.
                    if (item.Buffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(item.Buffer);
                    }
                }
            }

            // Once per batch, which is why Feed and Flush are separate.
            _h3.Flush();

            if (_h3.IsFaulted)
            {
                return;
            }
        }
    }

    private async Task AcceptStreamsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                QuicStream stream = await _quic.AcceptInboundStreamAsync(cancellationToken);
                _streams[stream.Id] = stream;
                _ = ReadStreamAsync(stream, cancellationToken);
            }
        }
        catch (Exception)
        {
            // The connection closed, which is how an accept loop ends.
        }
    }

    private async Task ReadStreamAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                int read = await stream.ReadAsync(buffer, cancellationToken);

                if (read == 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    await _ingress.Writer.WriteAsync(
                        new Inbound(stream.Id, null, 0, true, false), cancellationToken);
                    return;
                }

                await _ingress.Writer.WriteAsync(
                    new Inbound(stream.Id, buffer, read, false, false), cancellationToken);
            }
        }
        catch (Exception)
        {
            _ingress.Writer.TryWrite(new Inbound(stream.Id, null, 0, false, true));
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _egress.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_egress.Reader.TryRead(out Outbound item))
                {
                    if (_streams.TryGetValue(item.StreamId, out QuicStream? stream))
                    {
                        if (item.Length > 0)
                        {
                            await stream.WriteAsync(
                                item.Buffer.AsMemory(0, item.Length), item.Fin, cancellationToken);
                        }
                        else if (item.Fin)
                        {
                            stream.CompleteWrites();
                        }

                        // A finished request stream must be released, or its credit is never
                        // returned and the peer stalls after MaxInboundBidirectionalStreams
                        // requests. Unidirectional streams are the control and QPACK streams and
                        // live as long as the connection.
                        if (item.Fin && (item.StreamId & 0x3) == 0x0 && _streams.TryRemove(item.StreamId, out QuicStream? finished))
                        {
                            // Off the writer: tearing a stream down is slow enough that awaiting it
                            // here serialises every other request behind it.
                            _ = finished.DisposeAsync().AsTask();
                        }
                    }
                    ArrayPool<byte>.Shared.Return(item.Buffer);
                }
            }
        }
        catch (Exception)
        {
            // Peer went away mid-write.
        }
    }

    // --- IHttp3Transport -------------------------------------------------------------------------

    public long OpenUniStream()
        => _spareUniStreams.TryDequeue(out QuicStream? stream) ? stream.Id : -1;

    public void Send(long streamId, ReadOnlySpan<byte> data, bool fin)
    {
        // Copied because the span is borrowed and the write happens later.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, data.Length));
        data.CopyTo(buffer);
        _egress.Writer.TryWrite(new Outbound(streamId, buffer, data.Length, fin));
    }

    // MsQuic handles flow control and backpressure itself, so the other three keep their defaults.

    public async ValueTask DisposeAsync()
    {
        foreach (QuicStream stream in _streams.Values)
        {
            await stream.DisposeAsync();
        }
        await _quic.DisposeAsync();
    }
}
