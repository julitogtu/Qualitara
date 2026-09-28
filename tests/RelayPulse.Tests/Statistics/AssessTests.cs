using RelayPulse.Core.Statistics;

namespace RelayPulse.Tests.Statistics;

// λ values are synthetic unless a comment says they come from the seed (tools/oracle_poisson.py).
public class AssessTests
{
    [Fact]
    public void Assess_NullLambda_IsInsufficientHistory()
    {
        var result = Significance.Assess(12, null);

        Assert.Equal(Verdict.InsufficientHistory, result.Verdict);
        Assert.Equal(double.PositiveInfinity, result.RankKey);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(3.69)]
    public void Assess_LambdaBelowGate_IsNotEnoughVolumeEvenForExtremeCounts(double lambda)
    {
        Assert.Equal(Verdict.NotEnoughVolume, Significance.Assess(0, lambda).Verdict);
        Assert.Equal(Verdict.NotEnoughVolume, Significance.Assess(40, lambda).Verdict);
    }

    [Fact]
    public void Assess_ZeroEventsAt370_IsBelow()
    {
        // Just over the gate and over ln 40: an empty week is now detectable.
        Assert.Equal(Verdict.Below, Significance.Assess(0, 3.7).Verdict);
    }

    [Fact]
    public void Assess_Account8SevenAgainstItsRealBaseline_IsNormalNotAMinus36PercentAlarm()
    {
        // λ derived from account 8's own weeks, not hard-coded (tools/oracle_poisson.py --account 8).
        var lambda = Baseline.Lambda(BaselineTests.Account8Before0720);

        Assert.Equal(Verdict.Normal, Significance.Assess(7, lambda).Verdict);
    }

    [Fact]
    public void Assess_BelowGate_KeepsLambdaAndTailsButReportsNoBand()
    {
        // At λ=3 a count of 12 is outside [0,8] yet unflagged; showing that band would contradict the verdict.
        var result = Significance.Assess(12, 3.0);

        Assert.Equal(Verdict.NotEnoughVolume, result.Verdict);
        Assert.Null(result.Band);
        Assert.Equal(3.0, result.Lambda);
        Assert.NotNull(result.PHigh);
    }

    [Fact]
    public void Assess_AboveGate_ReportsTheBand()
    {
        Assert.Equal(new CountBand(5, 20), Significance.Assess(7, 10.8).Band);
    }

    [Fact]
    public void Assess_Account6SpikeAgainstTrimmedBaseline_IsAbove()
    {
        // Real: account 6, week 2026-06-01, 880 vs its own baseline λ=72.1 (tools/oracle_poisson.py).
        var result = Significance.Assess(880, 72.1);

        Assert.Equal(Verdict.Above, result.Verdict);
        Assert.False(double.IsNaN(result.RankKey));
        Assert.True(result.RankKey < 1.0);
    }

    [Fact]
    public void Assess_RankKey_EqualsMinOfTailsOverAlphas()
    {
        const int n = 14;
        const double lambda = 6.0;
        var expected = Math.Min(
            PoissonTail.Lower(n, lambda) / Significance.AlphaLow,
            PoissonTail.Upper(n, lambda) / Significance.AlphaHigh);

        Assert.Equal(expected, Significance.Assess(n, lambda).RankKey, 12);
    }

    [Fact]
    public void Assess_RankKey_EveryFlaggedRowSortsAboveEveryUnflaggedRow()
    {
        // Mix of all five verdicts across several λ; ascending RankKey must put all Above/Below
        // first, with NotEnoughVolume and InsufficientHistory never interleaving with flags.
        var rows = new List<Assessment>();
        foreach (var lambda in new double?[] { null, 0.0, 2.5, 3.0, 3.7, 6.0, 10.8, 25.0, 72.1, 500.0 })
        {
            for (var n = 0; n <= 600; n += n < 50 ? 1 : 25)
            {
                rows.Add(Significance.Assess(n, lambda));
            }
        }

        var verdicts = rows.Select(r => r.Verdict).ToHashSet();
        Assert.Equal(5, verdicts.Count);

        var flagged = rows.Where(r => r.Verdict is Verdict.Above or Verdict.Below).ToList();
        var unflagged = rows.Where(r => r.Verdict is not (Verdict.Above or Verdict.Below)).ToList();
        Assert.True(flagged.Max(r => r.RankKey) < unflagged.Min(r => r.RankKey));
    }

    [Fact]
    public void Thresholds_MatchPlan()
    {
        Assert.Equal(0.025, Significance.AlphaLow);
        Assert.Equal(0.005, Significance.AlphaHigh);
        Assert.Equal(3.7, Significance.MinLambda);
    }
}
