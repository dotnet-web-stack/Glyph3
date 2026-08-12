using System.Buffers;

namespace Glyph3;

/// <summary>
/// The encoding side of the dynamic table: what to insert, what to reference, and the instructions
/// that keep the peer's copy in step.
/// </summary>
/// <remarks>
/// Two rules keep this simple and safe.
///
/// <para>Only entries the peer has ACKNOWLEDGED are referenced. A reference travels on the response
/// stream while its insertion travels on the encoder stream, and QUIC orders neither against the
/// other - so referencing something unacknowledged could arrive first and block one of the peer's
/// streams. Waiting costs a round trip before an entry becomes usable and removes the risk
/// entirely, whatever the peer advertised about blocked streams.</para>
///
/// <para>Nothing is ever evicted. The table fills with the headers a server repeats and then stops,
/// so an entry can never disappear while the peer still holds a reference to it.</para>
/// </remarks>
internal sealed class QpackEncoder
{
    private readonly QpackDynamicTable _table;

    private int _acknowledged;

    internal QpackEncoder(int peerCapacity)
    {
        Capacity = peerCapacity;
        _table = new QpackDynamicTable(peerCapacity);
    }

    /// <summary>How much table the peer said it would hold.</summary>
    internal int Capacity { get; }

    internal int InsertCount => _table.InsertCount;

    internal int Acknowledged => _acknowledged;

    /// <summary>Apply the peer's decoder-stream instructions.</summary>
    internal QpackDecoderStreamReader.Result ApplyDecoderStream(ref ReadOnlySpan<byte> input)
        => QpackDecoderStreamReader.Apply(ref input, ref _acknowledged, _table.InsertCount);

    /// <summary>
    /// Decide whether to insert a header, returning the encoder-stream instruction to send.
    /// Returns 0 when nothing should be inserted.
    /// </summary>
    internal int TryInsert(Span<byte> instruction, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        if (_table.FindExact(name, value) >= 0 || !_table.CanInsert(name, value))
        {
            return 0;   // already there, or the table is full and we do not evict
        }

        // Insert With Literal Name: 01 H=0 namelen(5+), name, then the value.
        int w = Qpack.WriteInt(instruction, 0x40, 5, name.Length);
        name.CopyTo(instruction[w..]);
        w += name.Length;

        w += Qpack.WriteInt(instruction[w..], 0x00, 7, value.Length);
        value.CopyTo(instruction[w..]);
        w += value.Length;

        _table.Insert(name, value);

        return w;
    }

    /// <summary>
    /// The absolute index of an entry the peer is known to hold, or -1 when the header must be sent
    /// literally this time.
    /// </summary>
    internal int FindReferenceable(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        int index = _table.FindExact(name, value);

        return index >= 0 && index < _acknowledged ? index : -1;
    }
}
