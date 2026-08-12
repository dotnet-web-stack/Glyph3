namespace Glyph3;

/// <summary>
/// The QPACK dynamic table, for the encoding side only.
/// </summary>
/// <remarks>
/// Append-only: entries are inserted until the table is full and then it stops growing. An encoder
/// is never obliged to evict, and not evicting means no entry can be dropped while the peer still
/// references it - which is where the reference counting and acknowledgement tracking would
/// otherwise be. A server repeats a small set of headers, so the table fills with the useful ones
/// and stays there.
///
/// <para>Sizing follows RFC 9204 3.2.1: an entry costs its name plus its value plus 32 bytes.</para>
/// </remarks>
internal sealed class QpackDynamicTable
{
    private readonly List<(byte[] Name, byte[] Value)> _entries = [];

    private readonly int _capacity;

    private int _size;

    internal QpackDynamicTable(int capacity)
    {
        _capacity = capacity;
    }

    /// <summary>Entries inserted so far. Also the absolute index the next insert would take.</summary>
    internal int InsertCount => _entries.Count;

    /// <summary>Bytes the table currently holds, by the RFC's accounting.</summary>
    internal int Size => _size;

    internal static int EntrySize(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) => name.Length + value.Length + 32;

    /// <summary>Whether another entry of this size would fit.</summary>
    internal bool CanInsert(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        => _size + EntrySize(name, value) <= _capacity;

    /// <summary>
    /// Append an entry and return its absolute index, or -1 when it does not fit.
    /// </summary>
    internal int Insert(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        if (!CanInsert(name, value))
        {
            return -1;
        }

        _entries.Add((name.ToArray(), value.ToArray()));
        _size += EntrySize(name, value);

        return _entries.Count - 1;
    }

    /// <summary>Absolute index of an exact name and value match, or -1.</summary>
    internal int FindExact(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Name.AsSpan().SequenceEqual(name) && _entries[i].Value.AsSpan().SequenceEqual(value))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Absolute index of an entry with this name, or -1.</summary>
    internal int FindName(ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Name.AsSpan().SequenceEqual(name))
            {
                return i;
            }
        }

        return -1;
    }
}
