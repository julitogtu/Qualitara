using RelayPulse.Core.Statistics;

namespace RelayPulse.Core.Pulse;

/// <summary>
/// One aggregation row: events in one account-local week, at one grain, split by type and outcome.
/// <see cref="Location"/> is null on account-grain rows (the second GROUPING SETS set); the column
/// is NOT NULL in the schema, so null is unambiguous.
/// </summary>
public sealed record WeeklyCount(DateOnly WeekStart, string? Location, string EventType, string? Outcome, int Events);

/// <param name="HasEvents">False when the account has no events in any complete week up to the target.</param>
public sealed record PulseReport(bool HasEvents, SeriesPulse Account, IReadOnlyList<SeriesPulse> Locations);

/// <summary>The judged week for one series: the whole account (<see cref="Location"/> null) or one location.</summary>
/// <param name="BaselineWeeks">Prior complete weeks the baseline drew on (at most 12, before trimming).</param>
public sealed record SeriesPulse(
    string? Location,
    int Count,
    int BaselineWeeks,
    Assessment Assessment,
    IReadOnlyList<TypeSplit> ByType);

/// <param name="Outcomes">Per-type outcome counts; a NULL outcome is keyed <see cref="PulseBuilder.UnspecifiedOutcome"/>.</param>
public sealed record TypeSplit(string EventType, int Count, IReadOnlyDictionary<string, int> Outcomes);
