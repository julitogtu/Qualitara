using RelayPulse.Core.Statistics;

namespace RelayPulse.Core.Pulse;

/// <summary>
/// Turns aggregation rows into judged series for one account-week. Pure: no clock, no I/O.
/// </summary>
public static class PulseBuilder
{
    public const string UnspecifiedOutcome = "unspecified";

    /// <param name="completeWeeks">
    /// The account timezone's complete week Mondays, ascending. These define the zero-fill: every
    /// series gets a count for each of them, and nothing outside them is ever read.
    /// </param>
    /// <param name="week">The week being judged; must be one of <paramref name="completeWeeks"/>.</param>
    /// <param name="rows">
    /// Both grains from the aggregation query, covering every complete week up to
    /// <paramref name="week"/>: a week with no rows is read as zero events, and the location list
    /// and <see cref="PulseReport.HasEvents"/> come from these rows. Rows outside the window are ignored.
    /// </param>
    public static PulseReport Build(IReadOnlyList<DateOnly> completeWeeks, DateOnly week, IReadOnlyList<WeeklyCount> rows)
    {
        ArgumentNullException.ThrowIfNull(completeWeeks);
        ArgumentNullException.ThrowIfNull(rows);

        var targetIndex = IndexOf(completeWeeks, week);
        if (targetIndex < 0)
        {
            throw new ArgumentException($"{week:yyyy-MM-dd} is not a complete week.", nameof(week));
        }

        // Up to and including the target; weeks after it and partial weeks never enter.
        var inWindow = completeWeeks.Take(targetIndex + 1).ToHashSet();
        var relevant = rows.Where(r => inWindow.Contains(r.WeekStart)).ToList();
        var prior = completeWeeks.Take(targetIndex).ToList();

        if (relevant.Count == 0)
        {
            // No events at all is no history, not a zero-volume history.
            var empty = new SeriesPulse(null, 0, Math.Min(prior.Count, Baseline.Window), Significance.Assess(0, null), []);
            return new PulseReport(false, empty, []);
        }

        var account = Judge(null, relevant.Where(r => r.Location is null).ToList(), prior, week);
        var locations = relevant
            .Where(r => r.Location is not null)
            .GroupBy(r => r.Location!, StringComparer.Ordinal)
            .Select(g => Judge(g.Key, g.ToList(), prior, week))
            .OrderBy(s => s.Assessment.RankKey)
            .ThenBy(s => s.Location, StringComparer.Ordinal)
            .ToList();

        return new PulseReport(true, account, locations);
    }

    private static SeriesPulse Judge(string? location, List<WeeklyCount> rows, List<DateOnly> prior, DateOnly week)
    {
        var perWeek = rows
            .GroupBy(r => r.WeekStart)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Events));

        // Zero-fill: a prior complete week with no rows counts as 0 events. That is only true if
        // the caller fetched every complete week up to the target (see Build's contract); a
        // narrower fetch would turn "not fetched" into "silent" and drag the baseline to zero.
        var history = prior.Select(w => perWeek.GetValueOrDefault(w)).ToList();
        var count = perWeek.GetValueOrDefault(week);
        var byType = rows
            .Where(r => r.WeekStart == week)
            .GroupBy(r => r.EventType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TypeSplit(
                g.Key,
                g.Sum(r => r.Events),
                g.GroupBy(r => r.Outcome ?? UnspecifiedOutcome, StringComparer.Ordinal)
                    .OrderBy(o => o.Key, StringComparer.Ordinal)
                    .ToDictionary(o => o.Key, o => o.Sum(r => r.Events), StringComparer.Ordinal)))
            .ToList();

        return new SeriesPulse(
            location,
            count,
            Math.Min(history.Count, Baseline.Window),
            Significance.Assess(count, Baseline.Lambda(history)),
            byType);
    }

    private static int IndexOf(IReadOnlyList<DateOnly> weeks, DateOnly week)
    {
        for (var i = 0; i < weeks.Count; i++)
        {
            if (weeks[i] == week)
            {
                return i;
            }
        }

        return -1;
    }
}
