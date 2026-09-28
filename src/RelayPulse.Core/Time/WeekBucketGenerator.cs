namespace RelayPulse.Core.Time;

/// <summary>
/// Builds the week_buckets dimension: every local Mon–Sun week touched by the observation window,
/// with DST-correct UTC bounds from <see cref="TimeZoneInfo"/>. Pure — no clock, no I/O.
/// </summary>
public static class WeekBucketGenerator
{
    /// <param name="ianaTimezone">IANA id, e.g. <c>America/Chicago</c>.</param>
    /// <param name="windowStartUtc">Global MIN(occurred_at). Must be <see cref="DateTimeKind.Utc"/>.</param>
    /// <param name="windowEndUtc">Global MAX(occurred_at). Must be <see cref="DateTimeKind.Utc"/>.</param>
    public static IReadOnlyList<WeekBucket> Generate(
        string ianaTimezone, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ianaTimezone);
        EnsureUtc(windowStartUtc, nameof(windowStartUtc));
        EnsureUtc(windowEndUtc, nameof(windowEndUtc));
        if (windowEndUtc < windowStartUtc)
        {
            throw new ArgumentException("Window end precedes window start.", nameof(windowEndUtc));
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimezone);

        // The window in the zone's own calendar, inclusive on both ends.
        var firstLocalDay = LocalDate(windowStartUtc, zone);
        var lastLocalDay = LocalDate(windowEndUtc, zone);

        var buckets = new List<WeekBucket>();
        for (var week = MondayOf(firstLocalDay); week <= lastLocalDay; week = week.AddDays(7))
        {
            var nextWeek = week.AddDays(7);
            var isComplete = week >= firstLocalDay && week.AddDays(6) <= lastLocalDay;

            buckets.Add(new WeekBucket(
                ianaTimezone,
                week,
                LocalMidnightToUtc(week, zone),
                LocalMidnightToUtc(nextWeek, zone),
                isComplete));
        }

        return buckets;
    }

    public static DateOnly MondayOf(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    // US zones switch at 02:00, so midnight is always a valid, unambiguous local time. Handled
    // anyway so a zone that switches at midnight can't produce a gap or overlap between weeks.
    private static DateTime LocalMidnightToUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        while (zone.IsInvalidTime(local))
        {
            // Skipped by a spring-forward: the week starts at the first instant that exists.
            local = local.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Repeated by a fall-back: take the earlier instant (the larger offset).
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static void EnsureUtc(DateTime value, string name)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException($"Expected DateTimeKind.Utc, got {value.Kind}.", name);
        }
    }
}
