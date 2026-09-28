namespace RelayPulse.Core.Statistics;

/// <summary>
/// Exact Poisson tail probabilities. Terms are built by the recurrence
/// ln p(k) = ln p(k−1) + ln λ − ln k and summed relative to the largest term, so there are no
/// factorials and nothing overflows or underflows prematurely (λ = 500 and beyond).
/// </summary>
public static class PoissonTail
{
    // Stop summing the upper tail once a term adds less than this, relative to the sum so far.
    private const double Epsilon = 1e-17;

    /// <summary>P(X ≤ n).</summary>
    public static double Lower(int n, double lambda)
    {
        EnsureValid(lambda);
        if (n < 0)
        {
            return 0.0;
        }

        if (lambda == 0.0)
        {
            return 1.0;
        }

        // Every term is positive, so a direct sum is stable; scale by the largest (the mode, or
        // the last term when n sits below the mode) to keep exp() in range.
        var logLambda = Math.Log(lambda);
        var peak = LogPmf(Math.Min(n, (int)Math.Floor(lambda)), lambda, logLambda);
        var logTerm = -lambda;
        var sum = 0.0;
        for (var k = 0; k <= n; k++)
        {
            if (k > 0)
            {
                logTerm += logLambda - Math.Log(k);
            }

            sum += Math.Exp(logTerm - peak);
        }

        return Math.Min(1.0, Math.Exp(peak) * sum);
    }

    /// <summary>P(X ≥ n) = 1 − P(X ≤ n−1).</summary>
    public static double Upper(int n, double lambda)
    {
        EnsureValid(lambda);
        if (n <= 0)
        {
            return 1.0;
        }

        if (lambda == 0.0)
        {
            return 0.0;
        }

        // At or below the mean the upper tail is large and the complement loses nothing.
        if (n <= lambda)
        {
            return 1.0 - Lower(n - 1, lambda);
        }

        // Past the mean, 1 − P(X ≤ n−1) cancels catastrophically; sum the tail directly. Terms
        // shrink monotonically for k > λ, so stop once they stop mattering.
        var first = LogPmf(n, lambda, Math.Log(lambda));
        var term = 1.0;
        var sum = 0.0;
        for (var k = n; term > Epsilon * sum; k++)
        {
            sum += term;
            term *= lambda / (k + 1);
        }

        return Math.Exp(first) * sum;
    }

    private static double LogPmf(int k, double lambda, double logLambda)
    {
        var log = -lambda;
        for (var j = 1; j <= k; j++)
        {
            log += logLambda - Math.Log(j);
        }

        return log;
    }

    private static void EnsureValid(double lambda)
    {
        if (!double.IsFinite(lambda) || lambda < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lambda), lambda, "λ must be finite and non-negative.");
        }
    }
}
