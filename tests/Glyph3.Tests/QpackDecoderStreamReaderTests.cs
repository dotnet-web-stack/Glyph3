using Xunit;

namespace Glyph3.Tests;

/// <summary>
/// The peer's decoder stream, which tells us how much of our table it actually holds. Referencing
/// an entry it has not confirmed is what would block one of its streams.
/// </summary>
public class QpackDecoderStreamReaderTests
{
    [Fact]
    public void InsertCountIncrementRaisesTheAcknowledgedCount()
    {
        int acknowledged = 0;

        Assert.Equal(QpackDecoderStreamReader.Result.Done, Apply([0x03], ref acknowledged, inserted: 5));
        Assert.Equal(3, acknowledged);

        Assert.Equal(QpackDecoderStreamReader.Result.Done, Apply([0x02], ref acknowledged, inserted: 5));
        Assert.Equal(5, acknowledged);
    }

    [Fact]
    public void SectionAcknowledgmentAndCancellationAreConsumedButChangeNothing()
    {
        int acknowledged = 0;

        Assert.Equal(QpackDecoderStreamReader.Result.Done, Apply([0x84, 0x48], ref acknowledged, inserted: 5));
        Assert.Equal(0, acknowledged);
    }

    [Fact]
    public void AcknowledgingMoreThanWeSentIsAnError()
    {
        // The two sides would then disagree about the table, and nothing after it could be trusted.
        int acknowledged = 0;

        Assert.Equal(QpackDecoderStreamReader.Result.Error, Apply([0x09], ref acknowledged, inserted: 5));
    }

    [Fact]
    public void AZeroIncrementIsAnError()
    {
        int acknowledged = 0;

        Assert.Equal(QpackDecoderStreamReader.Result.Error, Apply([0x00], ref acknowledged, inserted: 5));
    }

    [Fact]
    public void APartialInstructionIsRetried()
    {
        int acknowledged = 0;

        // 6-bit prefix saturated, so a continuation byte is expected and absent.
        Assert.Equal(QpackDecoderStreamReader.Result.NeedMore, Apply([0x3f], ref acknowledged, inserted: 500));
        Assert.Equal(0, acknowledged);
    }

    private static QpackDecoderStreamReader.Result Apply(byte[] input, ref int acknowledged, int inserted)
    {
        ReadOnlySpan<byte> span = input;
        return QpackDecoderStreamReader.Apply(ref span, ref acknowledged, inserted);
    }
}
