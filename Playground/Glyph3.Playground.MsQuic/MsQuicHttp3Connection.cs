using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Quic;
using System.Threading.Channels;
using Glyph3;

namespace Glyph3.Playground.MsQuic;

/// <summary>
/// One HTTP/3 connection: MsQuic underneath, Glyph3 on top.
///
/// Glyph3 owns no I/O, so this is the whole bridge - roughly a hundred lines that read QUIC
/// streams into <see cref="Http3Connection.Feed"/> and write what comes back out. Everything
/// HTTP/3 (framing, QPACK, dispatch) is Glyph3's, and everything QUIC (handshake, TLS, loss
/// recovery, flow control) is MsQuic's.
/// </summary>
/// <remarks>
/// Three impedance mismatches are worth pointing at, because any transport bridge hits them:
///
/// <para><b>Glyph3 is single-threaded, MsQuic is not.</b> A connection is one state machine -
/// QPACK's table and the frame parsers span streams - so it cannot be fed from several stream
/// loops at once. Everything funnels through one channel and one consumer, which is where
/// serialisation happens.</para>
///
/// <para><b>Send is synchronous, WriteAsync is not.</b> Glyph3 hands over a borrowed span and
/// expects it gone by the time the call returns, so each write is copied into a pooled buffer and
/// queued. A single writer per connection drains it, which also keeps per-stream ordering.</para>
///
/// <para><b>OpenUniStream is synchronous, OpenOutboundStreamAsync is not.</b> HTTP/3 needs
/// unidirectional streams for control and QPACK, and Glyph3 asks for them mid-parse. So a few are
/// opened up front, before the connection starts, and handed out from there.</para>
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
        // Opened BEFORE Glyph3 exists, because OpenUniStream has to answer synchronously. Three is
        // what HTTP/3 defines (control, QPACK encoder, QPACK decoder); Glyph3 currently asks for
        // one, and the spares cost nothing since an unwritten stream is never announced to the peer.
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

    // The one place Glyph3 is called. Single consumer, so no locking anywhere in the library.
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

                    // The end-of-stream marker carries no buffer: it exists to deliver fin.
                    if (item.Buffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(item.Buffer);
                    }
                }
            }

            // Once per batch rather than per chunk - the whole reason Feed and Flush are separate.
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
            // The connection closed, which is how an accept loop always ends.
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
                    }
                    ArrayPool<byte>.Shared.Return(item.Buffer);
                }
            }
        }
        catch (Exception)
        {
            // Peer went away mid-write; the connection is finished either way.
        }
    }

    // --- IHttp3Transport -------------------------------------------------------------------------

    public long OpenUniStream()
        => _spareUniStreams.TryDequeue(out QuicStream? stream) ? stream.Id : -1;

    public void Send(long streamId, ReadOnlySpan<byte> data, bool fin)
    {
        // Copied because the span is borrowed: Glyph3 may reuse it the moment this returns, and
        // the actual write happens later on the writer task.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, data.Length));
        data.CopyTo(buffer);
        _egress.Writer.TryWrite(new Outbound(streamId, buffer, data.Length, fin));
    }

    // MsQuic manages flow-control windows itself, and applies backpressure through WriteAsync, so
    // ReleaseFlowControl, CanSend and SetStreamPaced all keep their defaults.

    public async ValueTask DisposeAsync()
    {
        foreach (QuicStream stream in _streams.Values)
        {
            await stream.DisposeAsync();
        }
        await _quic.DisposeAsync();
    }
}
