using System.Buffers;

namespace Glyph3;

/// <summary>
/// QPACK (RFC 9204) without the dynamic table: our SETTINGS advertise capacity 0, so a conforming
/// peer may only send static-table references and literals - which reduces the decoder to the
/// static table, prefixed integers, and Huffman. The encoder mirrors that: indexed :status where
/// the table has the code, literal name references otherwise, literal names for everything else,
/// no Huffman on output (legal, and keeps the writer trivial).
/// </summary>
internal static class Qpack
{
    // --- prefixed integers (RFC 7541 §5.1, reused by QPACK) ------------------------------------

    public static bool TryReadInt(ReadOnlySpan<byte> input, int prefixBits, out long value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (input.IsEmpty)
        {
            return false;
        }

        int max = (1 << prefixBits) - 1;
        long v = input[0] & max;
        consumed = 1;
        if (v < max)
        {
            value = v;
            return true;
        }

        int shift = 0;
        while (true)
        {
            if (consumed >= input.Length)
            {
                consumed = 0;
                return false;
            }
            byte b = input[consumed++];
            v += (long)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                value = v;
                return true;
            }
            if (shift > 56)
            {
                consumed = 0;
                return false;   // absurd - treat as malformed
            }
        }
    }

    public static int WriteInt(Span<byte> output, byte firstByteBits, int prefixBits, long value)
    {
        int max = (1 << prefixBits) - 1;
        if (value < max)
        {
            output[0] = (byte)(firstByteBits | value);
            return 1;
        }
        output[0] = (byte)(firstByteBits | max);
        value -= max;
        int w = 1;
        while (value >= 0x80)
        {
            output[w++] = (byte)(0x80 | (value & 0x7F));
            value >>= 7;
        }
        output[w++] = (byte)value;
        return w;
    }

    // --- field-section decoding ----------------------------------------------------------------

    /// <summary>
    /// Decode a whole encoded field section into <paramref name="request"/> (pseudo-headers routed
    /// to the dedicated fields, the rest appended). Returns false on malformed input or any
    /// dynamic-table reference (illegal against our capacity-0 advertisement).
    /// </summary>
    public static bool TryDecodeFieldSection(ReadOnlySpan<byte> input, Http3Request request)
        => TryDecodeFieldSection(input, request, null, 0);

    /// <summary>
    /// Decode a field section, resolving dynamic references against <paramref name="table"/>.
    /// </summary>
    /// <remarks>
    /// A null table is the capacity-0 contract: any dynamic reference is refused, because we told
    /// the peer we keep no table.
    ///
    /// <para>With a table, a Required Insert Count above what we have received is refused too. We
    /// advertise 0 blocked streams, so a conforming peer only references what it knows we hold; a
    /// peer that does otherwise is in error rather than merely early, and that is what keeps this a
    /// pure function instead of a suspended request.</para>
    /// </remarks>
    internal static bool TryDecodeFieldSection(ReadOnlySpan<byte> input, Http3Request request,
        QpackDynamicTable? table, int advertisedCapacity)
    {
        // Prefix: encoded Required Insert Count (8-bit prefix), then sign + Delta Base (7-bit).
        if (!TryReadInt(input, 8, out long encodedInsertCount, out int c1))
        {
            return false;
        }
        input = input[c1..];

        if (input.IsEmpty)
        {
            return false;
        }

        bool negative = (input[0] & 0x80) != 0;

        if (!TryReadInt(input, 7, out long deltaBase, out int c2))
        {
            return false;
        }
        input = input[c2..];

        if (!TryDecodeInsertCount(encodedInsertCount, table, advertisedCapacity, out long requiredInsertCount))
        {
            return false;
        }

        // Everything the section references is addressed relative to Base (RFC 9204 4.5.1.2).
        long baseIndex = negative ? requiredInsertCount - deltaBase - 1 : requiredInsertCount + deltaBase;

        if (baseIndex < 0)
        {
            return false;
        }

        while (!input.IsEmpty)
        {
            byte first = input[0];

            if ((first & 0x80) != 0)
            {
                // Indexed Field Line: 1 T index(6+). T=1 static, T=0 dynamic.
                if (!TryReadInt(input, 6, out long idx, out int consumed))
                {
                    return false;
                }

                if ((first & 0x40) != 0)
                {
                    if (idx >= QpackStatic.Table.Length)
                    {
                        return false;
                    }
                    input = input[consumed..];
                    (byte[] name, byte[] value) = QpackStatic.Table[idx];
                    request.AddField(name, value);
                    continue;
                }

                // Relative to Base, counting backwards (RFC 9204 4.5.2).
                if (!TryResolve(table, baseIndex - idx - 1, out ReadOnlySpan<byte> dynName, out ReadOnlySpan<byte> dynValue))
                {
                    return false;
                }
                input = input[consumed..];
                request.AddField(dynName, dynValue);
                continue;
            }

            if ((first & 0x40) != 0)
            {
                // Literal With Name Reference: 01 N T index(4+), then value string.
                bool staticName = (first & 0x10) != 0;

                if (!TryReadInt(input, 4, out long idx, out int consumed))
                {
                    return false;
                }

                ReadOnlySpan<byte> nameRef;

                if (staticName)
                {
                    if (idx >= QpackStatic.Table.Length)
                    {
                        return false;
                    }
                    nameRef = QpackStatic.Table[idx].Name;
                }
                else if (!TryResolve(table, baseIndex - idx - 1, out nameRef, out _))
                {
                    return false;
                }

                // Copied: resolving borrows from the table, and AddField may outlive that.
                byte[] heldName = nameRef.ToArray();

                input = input[consumed..];
                if (!TryDecodeString(ref input, 7, out byte[] value, out int valueLen))
                {
                    return false;
                }
                request.AddField(heldName, value.AsSpan(0, valueLen));
                ArrayPool<byte>.Shared.Return(value);
                continue;
            }

            if ((first & 0x20) != 0)
            {
                // Literal With Literal Name: 001 N H namelen(3+), name, then value string.
                if (!TryDecodeString(ref input, 3, out byte[] name, out int nameLen))
                {
                    return false;
                }
                if (!TryDecodeString(ref input, 7, out byte[] value, out int valueLen))
                {
                    ArrayPool<byte>.Shared.Return(name);
                    return false;
                }
                request.AddField(name.AsSpan(0, nameLen), value.AsSpan(0, valueLen));
                ArrayPool<byte>.Shared.Return(name);
                ArrayPool<byte>.Shared.Return(value);
                continue;
            }

            if ((first & 0x10) != 0)
            {
                // Post-Base Indexed Field Line: 0001 index(4+), counting forward from Base.
                if (!TryReadInt(input, 4, out long postIdx, out int postConsumed) ||
                    !TryResolve(table, baseIndex + postIdx, out ReadOnlySpan<byte> postName, out ReadOnlySpan<byte> postValue))
                {
                    return false;
                }
                input = input[postConsumed..];
                request.AddField(postName, postValue);
                continue;
            }

            {
                // Literal With Post-Base Name Reference: 0000 N index(3+), then the value.
                if (!TryReadInt(input, 3, out long postIdx, out int postConsumed) ||
                    !TryResolve(table, baseIndex + postIdx, out ReadOnlySpan<byte> postName, out _))
                {
                    return false;
                }

                byte[] heldName = postName.ToArray();

                input = input[postConsumed..];
                if (!TryDecodeString(ref input, 7, out byte[] postValue, out int postLen))
                {
                    return false;
                }
                request.AddField(heldName, postValue.AsSpan(0, postLen));
                ArrayPool<byte>.Shared.Return(postValue);
                continue;
            }
        }

        return true;
    }

    /// <summary>
    /// Reconstruct the Required Insert Count from its wrapped encoding (RFC 9204 4.5.1.1). It is
    /// sent modulo twice the table's entry capacity so it stays small, which has to be undone
    /// against how many insertions we have actually seen.
    /// </summary>
    private static bool TryDecodeInsertCount(long encoded, QpackDynamicTable? table, int advertisedCapacity,
        out long requiredInsertCount)
    {
        requiredInsertCount = 0;

        if (encoded == 0)
        {
            return true;   // references nothing dynamic, which is always decodable
        }

        if (table is null)
        {
            return false;  // we advertised no table, so nothing may be referenced
        }

        long maxEntries = advertisedCapacity / 32;
        if (maxEntries <= 0)
        {
            return false;
        }

        long fullRange = 2 * maxEntries;
        if (encoded > fullRange)
        {
            return false;
        }

        long maxValue = table.InsertCount + maxEntries;
        long maxWrapped = maxValue / fullRange * fullRange;

        requiredInsertCount = maxWrapped + encoded - 1;

        if (requiredInsertCount > maxValue)
        {
            if (requiredInsertCount <= fullRange)
            {
                return false;
            }
            requiredInsertCount -= fullRange;
        }

        if (requiredInsertCount == 0)
        {
            return false;
        }

        // Blocked references are refused rather than parked: we advertise 0 blocked streams, so a
        // conforming peer never sends one.
        return requiredInsertCount <= table.InsertCount;
    }

    private static bool TryResolve(QpackDynamicTable? table, long absoluteIndex,
        out ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
    {
        if (table is null || absoluteIndex < 0 || absoluteIndex > int.MaxValue)
        {
            name = default;
            value = default;
            return false;
        }

        return table.TryGet((int)absoluteIndex, out name, out value);
    }

    // A QPACK string: H bit ahead of an N-bit-prefix length, then bytes (Huffman-coded when H).
    // Output is a pooled buffer + length; the caller returns it.
    internal static bool TryDecodeString(ref ReadOnlySpan<byte> input, int prefixBits, out byte[] buffer, out int length)
    {
        buffer = [];
        length = 0;
        if (input.IsEmpty)
        {
            return false;
        }

        bool huffman = (input[0] & (1 << prefixBits)) != 0;
        if (!TryReadInt(input, prefixBits, out long len, out int consumed) || len > 256 * 1024)
        {
            return false;
        }
        input = input[consumed..];
        if (input.Length < len)
        {
            return false;
        }

        ReadOnlySpan<byte> raw = input[..(int)len];
        input = input[(int)len..];

        if (!huffman)
        {
            buffer = ArrayPool<byte>.Shared.Rent((int)len);
            raw.CopyTo(buffer);
            length = (int)len;
            return true;
        }

        buffer = ArrayPool<byte>.Shared.Rent(Huffman.MaxDecodedLength((int)len));
        length = Huffman.Decode(raw, buffer);
        if (length < 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = [];
            return false;
        }
        return true;
    }

    // --- field-section encoding (responses) ----------------------------------------------------

    private static readonly (int Status, byte Index)[] IndexedStatuses =
    [
        (103, 24), (200, 25), (304, 26), (404, 27), (503, 28),
        (100, 63), (204, 64), (206, 65), (302, 66), (400, 67), (403, 68), (421, 69), (425, 70), (500, 71),
    ];

    /// <summary>Encode a response's field section (prefix + :status + headers) into a pooled buffer.</summary>
    public static byte[] EncodeResponseFields(Http3Response response, out int written)
    {
        int cap = 2 + 8;
        foreach ((ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value) in response.Headers)
        {
            cap += 8 + name.Length + 8 + value.Length;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(cap);
        int w = 0;

        // Prefix: RIC 0, Delta Base 0 - the no-dynamic-table constant.
        buf[w++] = 0x00;
        buf[w++] = 0x00;

        // :status - indexed when the static table has it, literal with name ref (idx 24) otherwise.
        byte statusIdx = 0;
        foreach ((int status, byte idx) in IndexedStatuses)
        {
            if (status == response.Status)
            {
                statusIdx = idx;
                break;
            }
        }
        if (statusIdx != 0)
        {
            w += WriteInt(buf.AsSpan(w), 0xC0, 6, statusIdx);        // 1 T=1 index
        }
        else
        {
            w += WriteInt(buf.AsSpan(w), 0x50, 4, 24);               // 01 N=0 T=1 nameidx
            Span<byte> digits = stackalloc byte[3];
            System.Buffers.Text.Utf8Formatter.TryFormat(response.Status, digits, out int dlen);
            w += WriteInt(buf.AsSpan(w), 0x00, 7, dlen);             // H=0 value
            digits[..dlen].CopyTo(buf.AsSpan(w));
            w += dlen;
        }

        foreach ((ReadOnlyMemory<byte> nameM, ReadOnlyMemory<byte> valueM) in response.Headers)
        {
            // Literal With Literal Name: 001 N=0 H=0 namelen(3+), lowercased name, H=0 value(7+).
            ReadOnlySpan<byte> name = nameM.Span;
            w += WriteInt(buf.AsSpan(w), 0x20, 3, name.Length);
            foreach (byte b in name)
            {
                buf[w++] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
            }
            w += WriteInt(buf.AsSpan(w), 0x00, 7, valueM.Length);
            valueM.Span.CopyTo(buf.AsSpan(w));
            w += valueM.Length;
        }

        written = w;
        return buf;
    }
}
