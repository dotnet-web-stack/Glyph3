namespace Glyph3;

/// <summary>
/// A QPACK dynamic table. Serves both directions, which differ only in whether they evict.
/// </summary>
/// <remarks>
/// Entries are addressed by ABSOLUTE index: the count of entries ever inserted before this one, so
/// an index never changes meaning. Eviction drops the oldest, which raises
/// <see cref="OldestAbsoluteIndex"/> and leaves everything still present addressed as before.
///
/// <para>The decode side must evict exactly when the peer does, or the two tables disagree about
/// what an index means and every later header decodes to the wrong value. That is why
/// <see cref="InsertEvicting"/> exists: the peer has already decided the entry fits, so we make
/// room the same way it did.</para>
///
/// <para>The encode side uses <see cref="Insert"/>, which refuses rather than evicts. Nothing is
/// ever dropped while the peer might still reference it, which is what removes the reference
/// counting.</para>
///
/// <para>Sizing follows RFC 9204 3.2.1: an entry costs its name plus its value plus 32.</para>
/// </remarks>
internal sealed class QpackDynamicTable
{
    private readonly Queue<(byte[] Name, byte[] Value)> _entries = new();

    private int _capacity;

    private int _size;

    internal QpackDynamicTable(int capacity)
    {
        _capacity = capacity;
    }

    /// <summary>Entries ever inserted. Also the absolute index the next insert will take.</summary>
    internal int InsertCount { get; private set; }

    /// <summary>Bytes currently held, by the RFC's accounting.</summary>
    internal int Size => _size;

    /// <summary>How many entries are still present.</summary>
    internal int Count => _entries.Count;

    /// <summary>The lowest absolute index still addressable; everything below it was evicted.</summary>
    internal int OldestAbsoluteIndex => InsertCount - _entries.Count;

    internal int Capacity => _capacity;

    internal static int EntrySize(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value) => name.Length + value.Length + 32;

    /// <summary>
    /// Change the capacity, evicting whatever no longer fits. The peer drives this on its encoder
    /// stream, and may only raise it as far as we advertised.
    /// </summary>
    internal bool TrySetCapacity(int capacity, int advertised)
    {
        if (capacity < 0 || capacity > advertised)
        {
            return false;
        }

        _capacity = capacity;
        EvictTo(capacity);

        return true;
    }

    /// <summary>Whether another entry of this size would fit without evicting.</summary>
    internal bool CanInsert(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
        => _size + EntrySize(name, value) <= _capacity;

    /// <summary>
    /// Append without evicting: returns the absolute index, or -1 when it does not fit. The
    /// encoding side, where refusing is safer than dropping something the peer may reference.
    /// </summary>
    internal int Insert(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        if (!CanInsert(name, value))
        {
            return -1;
        }

        return Append(name, value);
    }

    /// <summary>
    /// Append, evicting the oldest entries until it fits. The decoding side, mirroring what the
    /// peer already did. Fails only when the entry could not fit an empty table.
    /// </summary>
    internal int InsertEvicting(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        int needed = EntrySize(name, value);

        if (needed > _capacity)
        {
            // RFC 9204 3.2.2: the peer may insert an entry larger than the table, which evicts
            // everything and stores nothing. The insert count still advances.
            EvictTo(0);
            InsertCount++;
            return -1;
        }

        EvictTo(_capacity - needed);

        return Append(name, value);
    }

    /// <summary>The entry at an absolute index, or false when it was evicted or never existed.</summary>
    internal bool TryGet(int absoluteIndex, out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        int offset = absoluteIndex - OldestAbsoluteIndex;

        if (offset < 0 || offset >= _entries.Count)
        {
            name = default;
            value = default;
            return false;
        }

        (byte[] entryName, byte[] entryValue) = _entries.ElementAt(offset);

        name = entryName;
        value = entryValue;
        return true;
    }

    /// <summary>Absolute index of an exact name and value match, or -1.</summary>
    internal int FindExact(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        int index = OldestAbsoluteIndex;

        foreach ((byte[] entryName, byte[] entryValue) in _entries)
        {
            if (entryName.AsSpan().SequenceEqual(name) && entryValue.AsSpan().SequenceEqual(value))
            {
                return index;
            }
            index++;
        }

        return -1;
    }

    /// <summary>Absolute index of an entry with this name, or -1.</summary>
    internal int FindName(ReadOnlySpan<byte> name)
    {
        int index = OldestAbsoluteIndex;

        foreach ((byte[] entryName, byte[] _) in _entries)
        {
            if (entryName.AsSpan().SequenceEqual(name))
            {
                return index;
            }
            index++;
        }

        return -1;
    }

    private int Append(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        _entries.Enqueue((name.ToArray(), value.ToArray()));
        _size += EntrySize(name, value);

        return InsertCount++;
    }

    private void EvictTo(int target)
    {
        while (_size > target && _entries.Count > 0)
        {
            (byte[] name, byte[] value) = _entries.Dequeue();
            _size -= EntrySize(name, value);
        }
    }
}
