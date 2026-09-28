using RelayPulse.Core.Pulse;
using RelayPulse.Core.Statistics;

namespace RelayPulse.Tests.Pulse;

// Synthetic series throughout: these pin zero-fill, windowing, ranking and splits, not seed goldens.
public class PulseBuilderTests
{
    // 14 complete Mondays: 2026-02-02 … 2026-05-04.
    private static readonly DateOnly[] Weeks =
        Enumerable.Range(0, 14).Select(i => new DateOnly(2026, 2, 2).AddDays(7 * i)).ToArray();

    private static readonly DateOnly Target = Weeks[^1];

    // One (location, week) with n calls, outcome "connected", plus the matching account-grain row.
    private static IEnumerable<WeeklyCount> Calls(string location, DateOnly week, int n, string? outcome = "connected") =>
        n == 0 ? [] : [new(week, location, "call_received", outcome, n), new(week, null, "call_received", outcome, n)];

    private static IEnumerable<WeeklyCount> Steady(string location, int perWeek, int weeks = 14) =>
        Weeks.Take(weeks).SelectMany(w => Calls(location, w, perWeek));

    [Fact]
    public void Build_LocationSilentInTargetWeek_GetsZeroFilledRowAndIsJudged()
    {
        var rows = Steady("Site A", 20, weeks: 13).ToList();

        var report = PulseBuilder.Build(Weeks, Target, rows);

        var a = Assert.Single(report.Locations);
        Assert.Equal("Site A", a.Location);
        Assert.Equal(0, a.Count);
        Assert.Equal(Verdict.Below, a.Assessment.Verdict);
        Assert.Empty(a.ByType);
    }

    [Fact]
    public void Build_WeeksWithNoEvents_CountAsZeroInTheBaseline()
    {
        // 10 on alternate weeks, 0 between. Zero-filled trimmed mean of the 12 prior weeks is 5;
        // skipping empty weeks instead would give 10.
        var rows = Weeks.Where((_, i) => i % 2 == 0).SelectMany(w => Calls("Site A", w, 10)).ToList();

        var report = PulseBuilder.Build(Weeks, Target, rows);

        Assert.Equal(5.0, report.Locations[0].Assessment.Lambda!.Value, 10);
        Assert.Equal(12, report.Locations[0].BaselineWeeks);
    }

    [Fact]
    public void Build_SevenPriorCompleteWeeks_IsInsufficientHistory()
    {
        var rows = Steady("Site A", 10).ToList();

        var report = PulseBuilder.Build(Weeks, Weeks[7], rows);

        Assert.Equal(7, report.Account.BaselineWeeks);
        Assert.Equal(Verdict.InsufficientHistory, report.Account.Assessment.Verdict);
        Assert.Equal(Verdict.InsufficientHistory, report.Locations[0].Assessment.Verdict);
    }

    [Fact]
    public void Build_EightPriorCompleteWeeks_IsJudged()
    {
        var report = PulseBuilder.Build(Weeks, Weeks[8], Steady("Site A", 10).ToList());

        Assert.Equal(Verdict.Normal, report.Account.Assessment.Verdict);
    }

    [Fact]
    public void Build_RowsOutsideTheCompleteWeeksOrAfterTarget_AreIgnored()
    {
        var rows = Steady("Site A", 10)
            .Concat(Calls("Site A", new DateOnly(2026, 1, 26), 500)) // partial edge bucket
            .Concat(Calls("Site A", Weeks[^1].AddDays(7), 500))       // after the target
            .ToList();

        var report = PulseBuilder.Build(Weeks, Target, rows);

        Assert.Equal(10, report.Account.Count);
        Assert.Equal(10.0, report.Account.Assessment.Lambda!.Value, 10);
    }

    [Fact]
    public void Build_AccountRow_UsesAccountGrainRows()
    {
        var rows = Steady("Site A", 10).Concat(Steady("Site B", 30)).ToList();

        var report = PulseBuilder.Build(Weeks, Target, rows);

        Assert.Null(report.Account.Location);
        Assert.Equal(40, report.Account.Count);
        Assert.Equal(40.0, report.Account.Assessment.Lambda!.Value, 10);
    }

    [Fact]
    public void Build_Locations_FlaggedFirstThenByRankKeyThenName()
    {
        var rows = Steady("Site C", 20)                                 // normal
            .Concat(Steady("Site B", 20, weeks: 13))                    // 0 in target → below
            .Concat(Steady("Site A", 20))                               // normal, ties with C
            .Concat(Steady("Site D", 1))                                // λ=1 → not enough volume
            .Concat(Steady("Site E", 20, weeks: 13)).Concat(Calls("Site E", Target, 60)) // above
            .ToList();

        var report = PulseBuilder.Build(Weeks, Target, rows);

        // E: P(X≥60 | 20)/α_high ≈ 1e-10 beats B: P(X≤0 | 20)/α_low ≈ 8e-8. A and C tie → by name.
        Assert.Equal(new[] { "Site E", "Site B", "Site A", "Site C", "Site D" }, report.Locations.Select(l => l.Location));
        Assert.All(report.Locations.Take(2), l => Assert.True(l.Assessment.RankKey < 1));
        Assert.Equal(Verdict.NotEnoughVolume, report.Locations[^1].Assessment.Verdict);
    }

    [Fact]
    public void Build_NullOutcome_IsCountedAsUnspecified()
    {
        var rows = Steady("Site A", 10)
            .Concat(Calls("Site A", Target, 3, outcome: null))
            .Append(new WeeklyCount(Target, "Site A", "lead_created", "converted", 2))
            .Append(new WeeklyCount(Target, null, "lead_created", "converted", 2))
            .ToList();

        var report = PulseBuilder.Build(Weeks, Target, rows);

        var a = report.Locations[0];
        Assert.Equal(15, a.Count);
        var calls = Assert.Single(a.ByType, t => t.EventType == "call_received");
        Assert.Equal(13, calls.Count);
        Assert.Equal(10, calls.Outcomes["connected"]);
        Assert.Equal(3, calls.Outcomes[PulseBuilder.UnspecifiedOutcome]);
        Assert.Equal(2, Assert.Single(a.ByType, t => t.EventType == "lead_created").Count);
        Assert.Equal(15, report.Account.ByType.Sum(t => t.Count));
    }

    [Fact]
    public void Build_NoRows_IsEmptyWithInsufficientHistoryNotZeroVolume()
    {
        var report = PulseBuilder.Build(Weeks, Target, []);

        Assert.False(report.HasEvents);
        Assert.Empty(report.Locations);
        Assert.Equal(0, report.Account.Count);
        Assert.Equal(12, report.Account.BaselineWeeks);
        Assert.Equal(Verdict.InsufficientHistory, report.Account.Assessment.Verdict);
    }

    [Fact]
    public void Build_EventsOnlyBeforeTheBaselineWindow_IsZeroVolumeNotEmpty()
    {
        // Site A's only events are in week 0; the target's 12-week baseline (weeks 1–12) is silent.
        // That is history — all zeros — not an absence of history, and Site A keeps its row.
        var rows = Calls("Site A", Weeks[0], 30).ToList();

        var report = PulseBuilder.Build(Weeks, Target, rows);

        Assert.True(report.HasEvents);
        Assert.Equal(Verdict.NotEnoughVolume, report.Account.Assessment.Verdict);
        Assert.Equal(0.0, report.Account.Assessment.Lambda);
        var a = Assert.Single(report.Locations);
        Assert.Equal("Site A", a.Location);
        Assert.Equal(0, a.Count);
    }

    [Fact]
    public void Build_TargetNotAmongCompleteWeeks_Throws()
    {
        Assert.Throws<ArgumentException>(() => PulseBuilder.Build(Weeks, new DateOnly(2026, 1, 26), []));
    }
}
