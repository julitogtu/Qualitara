namespace RelayPulse.Core.Statistics;

/// <param name="RankKey">
/// min(p_low/α_low, p_high/α_high). Below 1 exactly when flagged, so ascending order puts every
/// flagged row first, most extreme at the top. +∞ when no verdict was reached.
/// </param>
/// <param name="Lambda">Expected count; null when history is insufficient.</param>
/// <param name="Band">Counts that would be Normal at this λ; null unless the row was actually judged.</param>
/// <param name="PLow">P(X ≤ count); null when λ is null.</param>
/// <param name="PHigh">P(X ≥ count); null when λ is null.</param>
public sealed record Assessment(
    Verdict Verdict,
    double RankKey,
    double? Lambda = null,
    CountBand? Band = null,
    double? PLow = null,
    double? PHigh = null);
