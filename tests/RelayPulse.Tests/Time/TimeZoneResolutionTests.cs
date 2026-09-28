namespace RelayPulse.Tests.Time;

// T0 gate (PLAN.md §4/§5): week_buckets is generated with TimeZoneInfo from the seed's IANA ids.
// If this fails on a machine, fall back to an explicit IANA -> Windows map before building on it.
public class TimeZoneResolutionTests
{
    private static readonly DateTime Winter = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Summer = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    // Every timezone present in db/seed.sql, with its standard and summer UTC offset in hours.
    [Theory]
    [InlineData("America/Chicago", -6, -5)]
    [InlineData("America/Phoenix", -7, -7)]
    [InlineData("UTC", 0, 0)]
    [InlineData("America/Denver", -7, -6)]
    [InlineData("America/Los_Angeles", -8, -7)]
    [InlineData("America/New_York", -5, -4)]
    public void FindSystemTimeZoneById_WithSeedIanaId_ResolvesWithCorrectOffsets(
        string ianaId, int winterOffsetHours, int summerOffsetHours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);

        Assert.Equal(TimeSpan.FromHours(winterOffsetHours), zone.GetUtcOffset(Winter));
        Assert.Equal(TimeSpan.FromHours(summerOffsetHours), zone.GetUtcOffset(Summer));
    }

    [Fact]
    public void FindSystemTimeZoneById_Phoenix_NeverObservesDst()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");

        Assert.False(zone.IsDaylightSavingTime(Summer));
        Assert.False(zone.SupportsDaylightSavingTime);
    }
}
