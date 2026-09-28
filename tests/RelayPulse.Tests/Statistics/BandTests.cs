using RelayPulse.Core.Statistics;

namespace RelayPulse.Tests.Statistics;

public class BandTests
{
    // Goldens printed by `python tools/oracle_poisson.py` (linear scan over independent tails).
    [Theory]
    [InlineData(3.0, 0, 8)]   // synthetic λ
    [InlineData(6.0, 2, 13)]  // synthetic λ
    [InlineData(25.0, 16, 39)] // synthetic λ
    [InlineData(75.0, 59, 98)] // synthetic λ
    [InlineData(10.8, 5, 20)] // real: account 8's baseline for 2026-07-20 (BaselineTests); its 7 sits inside
    public void For_GoldenLambdas_GivesExpectedIntegerBand(double lambda, int lo, int hi)
    {
        Assert.Equal(new CountBand(lo, hi), Significance.BandFor(lambda));
    }

    [Fact]
    public void For_LambdaFrom01To500_IsNonNegativeAndOrdered()
    {
        for (var lambda = 0.1; lambda <= 500.0; lambda += 0.1)
        {
            var band = Significance.BandFor(lambda);

            Assert.True(band.Lo >= 0, $"λ={lambda}: lo={band.Lo}");
            Assert.True(band.Hi >= band.Lo, $"λ={lambda}: [{band.Lo},{band.Hi}]");
        }
    }

    [Theory]
    [InlineData(3.7)]
    [InlineData(6.0)]
    [InlineData(10.8)]
    [InlineData(25.0)]
    [InlineData(72.3)]
    [InlineData(500.0)]
    public void For_Edges_AgreeWithAssess(double lambda)
    {
        // The band is exactly the set of counts Assess calls Normal — one rule, two views.
        var band = Significance.BandFor(lambda);

        Assert.Equal(Verdict.Normal, Significance.Assess(band.Lo, lambda).Verdict);
        Assert.Equal(Verdict.Normal, Significance.Assess(band.Hi, lambda).Verdict);
        Assert.Equal(Verdict.Above, Significance.Assess(band.Hi + 1, lambda).Verdict);
        if (band.Lo > 0)
        {
            Assert.Equal(Verdict.Below, Significance.Assess(band.Lo - 1, lambda).Verdict);
        }
    }
}
