namespace Glyph3;

/// <summary>
/// Our decoder stream: what we tell the peer about its insertions (RFC 9204 4.4).
/// </summary>
/// <remarks>
/// Insert Count Increment is what makes a table usable at all when 0 blocked streams are
/// advertised. Until the peer knows we hold an entry it may not reference it, so an implementation
/// that never sends these compresses nothing while looking entirely correct.
/// </remarks>
internal static class QpackDecoderStream
{
    /// <summary>Insert Count Increment: 00 increment(6+).</summary>
    internal static int WriteInsertCountIncrement(Span<byte> output, long increment)
        => Qpack.WriteInt(output, 0x00, 6, increment);

    /// <summary>Section Acknowledgment: 1 stream id(7+).</summary>
    internal static int WriteSectionAcknowledgment(Span<byte> output, long streamId)
        => Qpack.WriteInt(output, 0x80, 7, streamId);

    /// <summary>Stream Cancellation: 01 stream id(6+).</summary>
    internal static int WriteStreamCancellation(Span<byte> output, long streamId)
        => Qpack.WriteInt(output, 0x40, 6, streamId);
}
