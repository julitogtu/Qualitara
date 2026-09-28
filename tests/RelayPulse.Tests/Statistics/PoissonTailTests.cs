using RelayPulse.Core.Statistics;

namespace RelayPulse.Tests.Statistics;

public class PoissonTailTests
{
    // λ values are synthetic unless noted: they pin the math, not the seed. Reference values are
    // printed by `python tools/oracle_poisson.py` ("Reference tails") — math.fsum of per-term
    // exp(k·ln λ − λ − lgamma(k+1)), an implementation independent of the C# recurrence.
    private const double Rel = 1e-9;

    private static void AssertRelative(double expected, double actual) =>
        Assert.True(Math.Abs(actual - expected) <= Rel * expected,
            $"expected {expected:R}, got {actual:R}");

    [Theory]
    [InlineData(2, 3.0, 0.42319008112684364)]
    [InlineData(0, 3.68, 0.025222974835227212)]
    [InlineData(0, 3.69, 0.024972002042276155)]
    [InlineData(40, 75.0, 6.872731725629959e-06)]
    [InlineData(400, 500.0, 2.0780896100118295e-06)]
    [InlineData(0, 500.0, 7.124576406741286e-218)] // e^−500: far below any absolute tolerance
    public void Lower_ReferenceValues_MatchIndependentImplementation(int n, double lambda, double expected)
    {
        AssertRelative(expected, PoissonTail.Lower(n, lambda));
    }

    [Theory]
    [InlineData(9, 3.0, 0.0038029920616759662)]
    [InlineData(14, 6.0, 0.003628492738722768)]
    [InlineData(600, 500.0, 7.785272561879036e-06)]
    [InlineData(60, 20.0, 4.233284694712773e-13)] // where 1 − Lower would keep ~3 digits
    public void Upper_ReferenceValues_MatchIndependentImplementation(int n, double lambda, double expected)
    {
        // Far upper tail: 1 − P(X≤n−1) would lose most digits to cancellation here.
        AssertRelative(expected, PoissonTail.Upper(n, lambda));
    }

    [Fact]
    public void Lower_ZeroEventsAtLn40_CrossesAlphaLowBetween368And369()
    {
        // n=0 is Below iff e^−λ < 0.025 iff λ > ln 40 = 3.6889.
        Assert.True(PoissonTail.Lower(0, 3.68) >= 0.025);
        Assert.True(PoissonTail.Lower(0, 3.69) < 0.025);
    }

    [Fact]
    public void Upper_Account6SpikeAgainstTrimmedBaseline_IsFiniteAndEffectivelyZero()
    {
        // Real: account 6, week 2026-06-01, against that week's own baseline (oracle: λ=72.1).
        var p = PoissonTail.Upper(880, 72.1);

        Assert.False(double.IsNaN(p));
        Assert.InRange(p, 0.0, 1e-300);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(3.0)]
    [InlineData(72.3)]
    [InlineData(500.0)]
    public void LowerAndUpper_AdjacentCounts_SumToOne(double lambda)
    {
        // P(X≤n) + P(X≥n+1) = 1 for every n: the two tails partition the distribution.
        for (var n = 0; n <= (int)(3 * lambda) + 10; n++)
        {
            Assert.Equal(1.0, PoissonTail.Lower(n, lambda) + PoissonTail.Upper(n + 1, lambda), 12);
        }
    }

    [Fact]
    public void Tails_NonPositiveCounts_AreTrivial()
    {
        Assert.Equal(0.0, PoissonTail.Lower(-1, 5.0));
        Assert.Equal(1.0, PoissonTail.Upper(0, 5.0));
        Assert.Equal(1.0, PoissonTail.Upper(-3, 5.0));
    }

    [Fact]
    public void Tails_ZeroLambda_IsPointMassAtZero()
    {
        // A baseline of all-zero weeks is legal input; it must not produce NaN.
        Assert.Equal(1.0, PoissonTail.Lower(0, 0.0));
        Assert.Equal(1.0, PoissonTail.Upper(0, 0.0));
        Assert.Equal(0.0, PoissonTail.Upper(1, 0.0));
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Tails_InvalidLambda_Throw(double lambda)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PoissonTail.Lower(3, lambda));
        Assert.Throws<ArgumentOutOfRangeException>(() => PoissonTail.Upper(3, lambda));
    }
}
