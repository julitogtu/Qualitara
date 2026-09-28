namespace RelayPulse.Core.Statistics;

/// <summary>Inclusive range of weekly counts that would be judged normal for a given λ.</summary>
public sealed record CountBand(int Lo, int Hi);
