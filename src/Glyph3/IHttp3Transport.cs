namespace Glyph3;

/// <summary>
/// Everything Glyph3 needs from a QUIC connection, and nothing else. Implement this over
/// <c>System.Net.Quic</c>, over io_uring, over a pair of in-memory queues for a test - Glyph3 does
/// no I/O of its own and owns no sockets, threads or timers.
/// </summary>
/// <remarks>
/// Only <see cref="OpenUniStream"/> and <see cref="Send"/> have to be implemented. The rest have
/// defaults that are correct for a transport which handles that concern itself: MsQuic manages
/// flow control and applies backpressure through its own write path, so a MsQuic host implements
/// two methods and inherits the other three.
///
/// <para><b>Buffers are borrowed, never retained.</b> <see cref="Send"/> receives a span that
/// Glyph3 may reuse the moment it returns, so an implementation copies or transmits synchronously.
/// This is deliberate: it lets a host holding kernel-owned memory pass it straight through instead
/// of copying into something Glyph3 could keep.</para>
///
/// <para><b>Not thread-safe, by design.</b> A connection is a single state machine - QPACK's
/// dynamic table and the frame parsers span streams - so all calls into
/// <see cref="Http3Connection"/> must be serialised. A transport that reads streams concurrently
/// (which MsQuic does) funnels them through one consumer; a single-threaded host pays nothing for
/// a lock it does not need.</para>
/// </remarks>
public interface IHttp3Transport
{
    /// <summary>
    /// Open a unidirectional stream and return its id, or a negative value if none can be opened.
    /// HTTP/3 needs three of these - control, QPACK encoder, QPACK decoder - and Glyph3 cannot
    /// create them itself, which is the one place this abstraction genuinely leaks.
    /// </summary>
    long OpenUniStream();

    /// <summary>
    /// Send on a stream, optionally closing the sending side. The span is borrowed for the
    /// duration of the call only.
    /// </summary>
    void Send(long streamId, ReadOnlySpan<byte> data, bool fin);

    /// <summary>
    /// Report that the application consumed <paramref name="bytes"/> of a request body, so the
    /// peer's flow-control window can reopen.
    ///
    /// The default does nothing, which is correct wherever the transport manages windows itself -
    /// MsQuic does. Implement it only where credit is manual.
    /// </summary>
    void ReleaseFlowControl(long streamId, int bytes) { }

    /// <summary>
    /// Whether another <see cref="Send"/> can be queued right now. Streamed responses check this
    /// and park rather than growing an unbounded queue; when it goes false, the host is expected
    /// to call <see cref="Http3Connection.OnSendCapacityAvailable"/> once room exists.
    ///
    /// The default says yes always, which suits a transport whose own write path blocks or buffers.
    /// </summary>
    bool CanSend => true;

    /// <summary>
    /// Hint that a stream should be paced - used while a streamed request body is being pulled, so
    /// the peer is not invited to outrun the handler. Advisory; the default ignores it.
    /// </summary>
    void SetStreamPaced(long streamId, bool paced) { }
}
