using RelayPulse.Core.Pulse;
using RelayPulse.Core.Statistics;

namespace RelayPulse.Api.Pulse;

/// <param name="Period">Null only when no data has been loaded at all.</param>
/// <param name="State"><c>ok</c>, or <c>empty</c> when the account has no events to judge.</param>
public sealed record PulseResponse(
    int AccountId,
    string AccountName,
    string Timezone,
    PulsePeriod? Period,
    string State,
    PulseRow Account,
    IReadOnlyList<PulseRow> Locations);

/// <param name="WeekEnd">Sunday, inclusive, in the account's timezone.</param>
/// <param name="PreviousWeek">Previous complete week, null at the start of the window.</param>
/// <param name="NextWeek">Next complete week, null at the last complete week.</param>
public sealed record PulsePeriod(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    bool IsDefault,
    DateOnly FirstCompleteWeek,
    DateOnly LastCompleteWeek,
    DateOnly? PreviousWeek,
    DateOnly? NextWeek);

/// <param name="Location">Null on the account row.</param>
/// <param name="Lambda">Expected count from the trimmed 12-week baseline; null with insufficient history.</param>
/// <param name="PLow">P(X ≤ count).</param>
/// <param name="PHigh">P(X ≥ count).</param>
public sealed record PulseRow(
    string? Location,
    int Count,
    Verdict Verdict,
    double? Lambda,
    CountBand? Band,
    double? PLow,
    double? PHigh,
    int BaselineWeeks,
    IReadOnlyList<TypeSplit> ByType)
{
    public static PulseRow From(SeriesPulse s) => new(
        s.Location,
        s.Count,
        s.Assessment.Verdict,
        s.Assessment.Lambda,
        s.Assessment.Band,
        s.Assessment.PLow,
        s.Assessment.PHigh,
        s.BaselineWeeks,
        s.ByType);
}
