using System.Buffers;

namespace Glyph3;

/// <summary>
/// The peer's encoder stream: the instructions that build our decode table (RFC 9204 4.3).
/// </summary>
/// <remarks>
/// A byte stream, so an instruction can straddle any number of reads. Anything incomplete leaves
/// the buffer untouched and is retried when more arrives, which is why every branch checks before
/// it consumes.
/// </remarks>
internal static class QpackEncoderStream
{
    internal enum Result
    {
        /// <summary>Everything available was applied.</summary>
        Done,

        /// <summary>A partial instruction remains; feed more bytes.</summary>
        NeedMore,

        /// <summary>The peer broke the rules. The connection is finished.</summary>
        Error,
    }

    /// <summary>
    /// Apply as many instructions as are complete, advancing <paramref name="input"/> past them.
    /// </summary>
    internal static Result Apply(ref ReadOnlySpan<byte> input, QpackDynamicTable table, int advertisedCapacity)
    {
        while (!input.IsEmpty)
        {
            byte first = input[0];

            if ((first & 0x80) != 0)
            {
                // Insert With Name Reference: 1 T index(6+), then the value.
                Result step = InsertWithNameReference(ref input, table);
                if (step != Result.Done)
                {
                    return step;
                }
            }
            else if ((first & 0x40) != 0)
            {
                // Insert With Literal Name: 01 H namelen(5+), name, then the value.
                Result step = InsertWithLiteralName(ref input, table);
                if (step != Result.Done)
                {
                    return step;
                }
            }
            else if ((first & 0x20) != 0)
            {
                // Set Dynamic Table Capacity: 001 capacity(5+).
                if (!Qpack.TryReadInt(input, 5, out long capacity, out int consumed))
                {
                    return Result.NeedMore;
                }
                if (capacity > int.MaxValue || !table.TrySetCapacity((int)capacity, advertisedCapacity))
                {
                    return Result.Error;
                }
                input = input[consumed..];
            }
            else
            {
                // Duplicate: 000 index(5+). Re-inserts an existing entry so it survives eviction.
                if (!Qpack.TryReadInt(input, 5, out long relative, out int consumed))
                {
                    return Result.NeedMore;
                }
                if (!TryDuplicate(table, relative))
                {
                    return Result.Error;
                }
                input = input[consumed..];
            }
        }

        return Result.Done;
    }

    private static Result InsertWithNameReference(ref ReadOnlySpan<byte> input, QpackDynamicTable table)
    {
        bool isStatic = (input[0] & 0x40) != 0;

        if (!Qpack.TryReadInt(input, 6, out long index, out int consumed))
        {
            return Result.NeedMore;
        }

        ReadOnlySpan<byte> rest = input[consumed..];

        if (!Qpack.TryDecodeString(ref rest, 7, out byte[] value, out int valueLen))
        {
            return Result.NeedMore;
        }

        Result result = Result.Done;

        if (TryResolveName(table, isStatic, index, out ReadOnlySpan<byte> name))
        {
            // Copied first: inserting can evict the entry the name was borrowed from.
            byte[] nameCopy = name.ToArray();
            table.InsertEvicting(nameCopy, value.AsSpan(0, valueLen));
        }
        else
        {
            result = Result.Error;
        }

        ArrayPool<byte>.Shared.Return(value);

        if (result == Result.Done)
        {
            input = rest;
        }

        return result;
    }

    private static Result InsertWithLiteralName(ref ReadOnlySpan<byte> input, QpackDynamicTable table)
    {
        ReadOnlySpan<byte> rest = input;

        if (!Qpack.TryDecodeString(ref rest, 5, out byte[] name, out int nameLen))
        {
            return Result.NeedMore;
        }

        if (!Qpack.TryDecodeString(ref rest, 7, out byte[] value, out int valueLen))
        {
            ArrayPool<byte>.Shared.Return(name);
            return Result.NeedMore;
        }

        table.InsertEvicting(name.AsSpan(0, nameLen), value.AsSpan(0, valueLen));

        ArrayPool<byte>.Shared.Return(name);
        ArrayPool<byte>.Shared.Return(value);

        input = rest;
        return Result.Done;
    }

    private static bool TryDuplicate(QpackDynamicTable table, long relative)
    {
        // Relative to the newest entry, per RFC 9204 3.2.5.
        int absolute = table.InsertCount - 1 - (int)relative;

        if (relative < 0 || !table.TryGet(absolute, out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value))
        {
            return false;
        }

        // Copied out first: inserting can evict the very entry being duplicated.
        byte[] nameCopy = name.ToArray();
        byte[] valueCopy = value.ToArray();

        table.InsertEvicting(nameCopy, valueCopy);

        return true;
    }

    private static bool TryResolveName(QpackDynamicTable table, bool isStatic, long index, out ReadOnlySpan<byte> name)
    {
        if (isStatic)
        {
            if (index < 0 || index >= QpackStatic.Table.Length)
            {
                name = default;
                return false;
            }

            name = QpackStatic.Table[index].Name;
            return true;
        }

        int absolute = table.InsertCount - 1 - (int)index;

        return table.TryGet(absolute, out name, out _);
    }
}
