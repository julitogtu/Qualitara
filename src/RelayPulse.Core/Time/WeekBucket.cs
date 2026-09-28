namespace RelayPulse.Core.Time;

/// <summary>
/// One Mon–Sun week in a timezone's local calendar, expressed as a half-open UTC range
/// [<see cref="UtcStart"/>, <see cref="UtcEnd"/>) so events can be bucketed with a range join.
/// </summary>
/// <param name="IsComplete">
/// True only when all 7 local days fall inside the observation window. A property of the window,
/// not of where events land: a complete week can hold zero events.
/// </param>
public sealed record WeekBucket(
    string Timezone,
    DateOnly WeekStartLocal,
    DateTime UtcStart,
    DateTime UtcEnd,
    bool IsComplete);
