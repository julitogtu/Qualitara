using RelayPulse.Core.Time;

namespace RelayPulse.Tests.Time;

public class WeekBucketGeneratorTests
{
    // Global MIN / MAX(occurred_at) of db/seed.sql (tools/profile_seed.py).
    private static readonly DateTime SeedMin = Utc(2026, 2, 1, 10, 57, 44);
    private static readonly DateTime SeedMax = Utc(2026, 7, 27, 22, 20, 34);

    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) =>
        new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    [Theory]
    [InlineData("America/Chicago")]
    [InlineData("America/Denver")]
    [InlineData("America/Los_Angeles")]
    [InlineData("America/New_York")]
    [InlineData("America/Phoenix")]
    [InlineData("UTC")]
    public void Generate_SeedWindow_Gives27BucketsWith25CompleteAndBothEdgesPartial(string timezone)
    {
        var buckets = WeekBucketGenerator.Generate(timezone, SeedMin, SeedMax);

        Assert.Equal(27, buckets.Count);
        Assert.Equal(25, buckets.Count(b => b.IsComplete));
        Assert.Equal(new DateOnly(2026, 1, 26), buckets[0].WeekStartLocal);
        Assert.False(buckets[0].IsComplete);
        Assert.Equal(new DateOnly(2026, 7, 27), buckets[^1].WeekStartLocal);
        Assert.False(buckets[^1].IsComplete);
        Assert.Equal(new DateOnly(2026, 7, 20), buckets.Last(b => b.IsComplete).WeekStartLocal);
    }

    [Theory]
    [InlineData("America/Chicago")]
    [InlineData("America/Phoenix")]
    [InlineData("UTC")]
    public void Generate_Always_ProducesContiguousMondayBucketsWithNoGapOrOverlap(string timezone)
    {
        var buckets = WeekBucketGenerator.Generate(timezone, SeedMin, SeedMax);

        Assert.All(buckets, b => Assert.Equal(DayOfWeek.Monday, b.WeekStartLocal.DayOfWeek));
        Assert.All(buckets, b => Assert.Equal(DateTimeKind.Utc, b.UtcStart.Kind));
        for (var i = 1; i < buckets.Count; i++)
        {
            Assert.Equal(buckets[i - 1].UtcEnd, buckets[i].UtcStart);
        }
    }

    [Fact]
    public void Generate_ChicagoSpringForwardWeek_Is167HoursAndShiftsOffset()
    {
        var buckets = WeekBucketGenerator.Generate("America/Chicago", SeedMin, SeedMax);

        // DST starts Sun 2026-03-08: that week starts at CST (-6) and ends at CDT (-5).
        var dstWeek = buckets.Single(b => b.WeekStartLocal == new DateOnly(2026, 3, 2));

        Assert.Equal(Utc(2026, 3, 2, 6), dstWeek.UtcStart);
        Assert.Equal(Utc(2026, 3, 9, 5), dstWeek.UtcEnd);
        Assert.Equal(TimeSpan.FromHours(167), dstWeek.UtcEnd - dstWeek.UtcStart);
    }

    [Fact]
    public void Generate_Phoenix_EveryWeekIs168HoursAtMinus7()
    {
        var buckets = WeekBucketGenerator.Generate("America/Phoenix", SeedMin, SeedMax);

        Assert.All(buckets, b => Assert.Equal(TimeSpan.FromHours(168), b.UtcEnd - b.UtcStart));
        Assert.All(buckets, b => Assert.Equal(7, b.UtcStart.Hour));
    }

    [Fact]
    public void Generate_WindowStartingEarlyMondayUtc_OpensInTheLocalSundaysWeek()
    {
        // 03:00Z Mon 02-09 is Sun 02-08 21:00 in Chicago. Bucketing in UTC would open the window
        // in the week of 02-09; the local calendar opens it in the week of 02-02, on its last day.
        var buckets = WeekBucketGenerator.Generate(
            "America/Chicago", Utc(2026, 2, 9, 3), Utc(2026, 3, 1, 12));

        Assert.Equal(new DateOnly(2026, 2, 2), buckets[0].WeekStartLocal);
        Assert.False(buckets[0].IsComplete);
    }

    [Fact]
    public void Generate_WindowOpeningAtLocalMondayMidnight_MakesFirstWeekComplete()
    {
        // Completeness is a property of the window: Mon 00:00 local through Sun covers all 7 days.
        var buckets = WeekBucketGenerator.Generate(
            "America/Chicago", Utc(2026, 2, 2, 6), Utc(2026, 2, 9, 5, 59, 59));

        Assert.Single(buckets);
        Assert.True(buckets[0].IsComplete);
    }

    [Fact]
    public void Generate_NonUtcInput_Throws()
    {
        var local = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.Throws<ArgumentException>(() =>
            WeekBucketGenerator.Generate("UTC", local, SeedMax));
    }
}
