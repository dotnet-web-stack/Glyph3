namespace Glyph3;

/// <summary>
/// What Glyph3 needs from a QUIC connection. Implement over System.Net.Quic, io_uring, or
/// in-memory queues for a test.
/// </summary>
/// <remarks>
/// Only <see cref="OpenUniStream"/> and <see cref="Send"/> must be implemented; the defaults suit a
/// transport that handles those concerns itself.
///
/// <para>Spans passed to <see cref="Send"/> are borrowed for the call only. Copy or transmit
/// synchronously.</para>
///
/// <para>Not thread-safe. A connection is one state machine, so the host must serialise all calls
/// into <see cref="Http3Connection"/>.</para>
/// </remarks>
public interface IHttp3Transport
{
    /// <summary>
    /// Open a unidirectional stream, or return a negative value if none can be opened yet. HTTP/3
    /// uses these for its control and QPACK streams.
    /// </summary>
    long OpenUniStream();

    /// <summary>Send on a stream, optionally closing the sending side.</summary>
    void Send(long streamId, ReadOnlySpan<byte> data, bool fin);

    /// <summary>
    /// Report body bytes consumed so the peer's flow-control window reopens. Default does nothing,
    /// which is correct where the transport manages windows itself.
    /// </summary>
    void ReleaseFlowControl(long streamId, int bytes) { }

    /// <summary>
    /// Whether another <see cref="Send"/> can be queued. Streamed responses park when this is
    /// false and resume on <see cref="Http3Connection.OnSendCapacityAvailable"/>.
    /// </summary>
    bool CanSend => true;

    /// <summary>Advisory: pace a stream while its body is being pulled.</summary>
    void SetStreamPaced(long streamId, bool paced) { }
}
