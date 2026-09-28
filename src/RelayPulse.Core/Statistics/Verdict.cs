namespace RelayPulse.Core.Statistics;

public enum Verdict
{
    Normal,
    Above,
    Below,

    /// <summary>λ below <see cref="Significance.MinLambda"/>: only large swings would be detectable.</summary>
    NotEnoughVolume,

    /// <summary>Fewer than <see cref="Baseline.MinWeeks"/> prior complete weeks.</summary>
    InsufficientHistory,
}
