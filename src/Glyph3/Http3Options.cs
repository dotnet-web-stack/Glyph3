namespace Glyph3;

/// <summary>
/// Tunables for one <see cref="Http3Connection"/>.
/// </summary>
/// <remarks>
/// The defaults turn the QPACK dynamic table off, which is the simple and stateless profile: peers
/// encode every header with static-table references and literals.
/// </remarks>
public sealed record Http3Options
{
    internal static readonly Http3Options Default = new();

    /// <summary>
    /// QPACK dynamic-table capacity in bytes, advertised to the peer in SETTINGS.
    ///
    /// 0 (the default) disables the mechanism entirely: nothing is inserted, no encoder or decoder
    /// stream is opened, and a peer that sends a dynamic reference anyway is refused. A nonzero
    /// capacity (4096 is conventional) lets peers compress repeated headers - cookies and
    /// user-agent, mostly - to about two bytes, at the cost of that much memory per connection.
    /// </summary>
    public int QpackDynamicTableCapacity { get; init; }

    /// <summary>
    /// How many of the peer's header blocks may wait on an insertion we have not acknowledged yet.
    ///
    /// 0 (the default) means a peer may build a table but may only reference entries we have
    /// acknowledged, so no request ever waits on one. It costs the peer a round trip before a new
    /// entry becomes usable, and it is what keeps decoding a pure function: a header block either
    /// decodes against the table we hold or it is a protocol error.
    ///
    /// Nonzero permits the peer to reference entries in flight, which compresses the opening burst
    /// of a connection but lets a lost packet stall an unrelated request.
    /// </summary>
    public int QpackBlockedStreams { get; init; }

    internal bool DynamicTableEnabled => QpackDynamicTableCapacity > 0;
}
