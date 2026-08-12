namespace Glyph3;

/// <summary>
/// The peer's decoder stream: what it tells us about the entries we inserted (RFC 9204 4.4).
/// </summary>
/// <remarks>
/// Only the insert count matters here. Until the peer acknowledges an entry we must not reference
/// it, or that reference could arrive before the insertion and block one of its streams. Waiting
/// for the acknowledgement costs a round trip and means we never do that, whatever the peer
/// advertised about blocked streams.
/// </remarks>
internal static class QpackDecoderStreamReader
{
    internal enum Result
    {
        Done,
        NeedMore,
        Error,
    }

    /// <summary>
    /// Apply the instructions available, advancing <paramref name="input"/> past them and raising
    /// <paramref name="acknowledged"/> as the peer confirms insertions.
    /// </summary>
    internal static Result Apply(ref ReadOnlySpan<byte> input, ref int acknowledged, int inserted)
    {
        while (!input.IsEmpty)
        {
            byte first = input[0];

            if ((first & 0x80) != 0)
            {
                // Section Acknowledgment: 1 stream id(7+). A section the peer decoded, which means
                // it holds everything that section referenced - but the increment instruction is
                // what states how much, so nothing is inferred from this.
                if (!Qpack.TryReadInt(input, 7, out _, out int consumed))
                {
                    return Result.NeedMore;
                }
                input = input[consumed..];
            }
            else if ((first & 0x40) != 0)
            {
                // Stream Cancellation: 01 stream id(6+). The peer gave up on a stream; its
                // references are released, which changes nothing we track.
                if (!Qpack.TryReadInt(input, 6, out _, out int consumed))
                {
                    return Result.NeedMore;
                }
                input = input[consumed..];
            }
            else
            {
                // Insert Count Increment: 00 increment(6+).
                if (!Qpack.TryReadInt(input, 6, out long increment, out int consumed))
                {
                    return Result.NeedMore;
                }

                // Acknowledging more than we ever sent means the two sides disagree about the
                // table, and nothing decoded after that could be trusted.
                if (increment <= 0 || acknowledged + increment > inserted)
                {
                    return Result.Error;
                }

                acknowledged += (int)increment;
                input = input[consumed..];
            }
        }

        return Result.Done;
    }
}
