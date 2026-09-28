using RelayPulse.Core.Statistics;

namespace RelayPulse.Tests.Statistics;

public class BaselineTests
{
    // Account 6, deduped, account-local weeks 2026-03-16 … 2026-06-01: the 12 complete weeks
    // before 2026-06-08 (printed by tools/oracle_poisson.py). The last is the 880 spike.
    private static readonly int[] Account6Before0608 = [81, 71, 82, 76, 55, 64, 86, 68, 78, 62, 53, 880];

    // Account 8, deduped, the 12 complete weeks 2026-04-27 … 2026-07-13 (printed by tools/oracle_poisson.py).
    internal static readonly int[] Account8Before0720 = [13, 14, 9, 12, 12, 10, 8, 10, 13, 11, 8, 10];

    [Fact]
    public void Lambda_Account8DefaultWeek_Is108()
    {
        // Drop one 8 and the 14 → 108 / 10. The old exclude-anomaly baseline was 10.4.
        Assert.Equal(10.8, Baseline.Lambda(Account8Before0720)!.Value, 10);
    }

    [Fact]
    public void Lambda_Account6WeekAfterSpike_IsTrimmedMean723NotPlainMean138()
    {
        var lambda = Baseline.Lambda(Account6Before0608);

        Assert.NotNull(lambda);
        Assert.Equal(72.3, lambda.Value, 10);
        Assert.Equal(138.0, Account6Before0608.Average(), 10); // guards the fixture itself
    }

    [Fact]
    public void Lambda_SevenWeeks_IsInsufficientHistory()
    {
        Assert.Null(Baseline.Lambda([10, 11, 12, 13, 14, 15, 16]));
    }

    [Fact]
    public void Lambda_NoWeeks_IsInsufficientHistory()
    {
        Assert.Null(Baseline.Lambda([]));
    }

    [Fact]
    public void Lambda_EightWeeks_DropsExtremesAndAveragesSix()
    {
        // Drop 1 and 100 → mean of 10,10,10,20,20,20 = 15.
        Assert.Equal(15.0, Baseline.Lambda([10, 100, 20, 10, 1, 20, 10, 20])!.Value, 10);
    }

    [Fact]
    public void Lambda_MoreThanTwelveWeeks_UsesOnlyTheTwelveMostRecent()
    {
        // Oldest-first input: the three leading 1000s are outside the trailing window.
        int[] weeks = [1000, 1000, 1000, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5];

        Assert.Equal(5.0, Baseline.Lambda(weeks)!.Value, 10);
    }

    [Fact]
    public void Lambda_TiedExtremes_DropsExactlyOneOfEach()
    {
        // Sorted: 2,2,4,4,4,4,8,8 → drop one 2 and one 8 → (2+4·4+8)/6.
        Assert.Equal(26.0 / 6, Baseline.Lambda([2, 8, 4, 2, 4, 8, 4, 4])!.Value, 10);
    }

    [Fact]
    public void Lambda_AllZeroWeeks_IsZeroNotNull()
    {
        // Enough history, no volume: that is a NotEnoughVolume verdict, not InsufficientHistory.
        Assert.Equal(0.0, Baseline.Lambda(new int[12])!.Value);
    }

    [Fact]
    public void Lambda_NegativeCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Baseline.Lambda([5, 5, 5, 5, 5, 5, 5, -1]));
    }
}
