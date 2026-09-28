namespace RelayPulse.Core.Statistics;

/// <summary>
/// Expected weekly rate λ from a series' own history: the trailing <see cref="Window"/> complete
/// weeks, one highest and one lowest dropped, mean of the rest. The trim is what keeps a single
/// anomaly week (acct 6, 2026-06-01) from inflating the baselines that follow it.
/// </summary>
public static class Baseline
{
    public const int Window = 12;
    public const int MinWeeks = 8;

    /// <param name="priorCompleteWeeks">
    /// Counts for complete weeks strictly before the week being judged, oldest first. The caller
    /// excludes partial weeks; only the last <see cref="Window"/> are used.
    /// </param>
    /// <returns>λ, or <c>null</c> when fewer than <see cref="MinWeeks"/> weeks are available.</returns>
    public static double? Lambda(IReadOnlyList<int> priorCompleteWeeks)
    {
        ArgumentNullException.ThrowIfNull(priorCompleteWeeks);
        if (priorCompleteWeeks.Any(c => c < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(priorCompleteWeeks), "Weekly counts cannot be negative.");
        }

        if (priorCompleteWeeks.Count < MinWeeks)
        {
            return null;
        }

        var sorted = priorCompleteWeeks
            .Skip(Math.Max(0, priorCompleteWeeks.Count - Window))
            .Order()
            .ToArray();

        return sorted[1..^1].Average();
    }
}
