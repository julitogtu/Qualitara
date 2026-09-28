namespace RelayPulse.Core.Statistics;

/// <summary>
/// Exact Poisson tail tests against the baseline λ. Asymmetric on purpose: the low tail
/// (α = 0.025) is 5× more sensitive than the high tail (α = 0.005).
/// </summary>
public static class Significance
{
    public const double AlphaLow = 0.025;
    public const double AlphaHigh = 0.005;

    /// <summary>
    /// Volume gate. Just above ln(1/α_low) = 3.689, the λ at which an empty week first becomes
    /// detectable as Below.
    /// </summary>
    public const double MinLambda = 3.7;

    /// <summary>The integer counts <see cref="Assess"/> would call Normal, ignoring the volume gate.</summary>
    public static CountBand BandFor(double lambda)
    {
        // Lower(n) rises with n and Upper(n) falls, so both edges are binary searches.
        // lo: smallest n with P(X ≤ n) ≥ α_low. P(X ≤ ⌈λ⌉) ≥ ~½, so it is never above ⌈λ⌉.
        var lo = FirstWhere(0, (int)Math.Ceiling(lambda), n => PoissonTail.Lower(n, lambda) >= AlphaLow);

        // hi: one below the smallest n with P(X ≥ n) < α_high. λ + 10√λ + 20 is far past it.
        var ceiling = (int)Math.Ceiling(lambda + 10 * Math.Sqrt(lambda) + 20);
        var hi = FirstWhere(1, ceiling, n => PoissonTail.Upper(n, lambda) < AlphaHigh) - 1;

        return new CountBand(lo, hi);
    }

    public static Assessment Assess(int count, double? lambda)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (lambda is not { } l)
        {
            return new Assessment(Verdict.InsufficientHistory, double.PositiveInfinity);
        }

        var pLow = PoissonTail.Lower(count, l);
        var pHigh = PoissonTail.Upper(count, l);

        if (l < MinLambda)
        {
            // No band: a gated row is never flagged, so "counts outside this range get flagged" would be false.
            return new Assessment(Verdict.NotEnoughVolume, double.PositiveInfinity, l, null, pLow, pHigh);
        }

        var band = BandFor(l);

        var verdict = pLow < AlphaLow ? Verdict.Below
            : pHigh < AlphaHigh ? Verdict.Above
            : Verdict.Normal;

        return new Assessment(verdict, Math.Min(pLow / AlphaLow, pHigh / AlphaHigh), l, band, pLow, pHigh);
    }

    // Smallest n in [from, to] for a monotone predicate (false…false, true…true); `to` if none holds.
    private static int FirstWhere(int from, int to, Func<int, bool> predicate)
    {
        while (from < to)
        {
            var mid = from + (to - from) / 2;
            if (predicate(mid))
            {
                to = mid;
            }
            else
            {
                from = mid + 1;
            }
        }

        return from;
    }
}
