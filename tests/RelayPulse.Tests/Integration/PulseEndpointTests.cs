using System.Net;
using System.Text.Json;

namespace RelayPulse.Tests.Integration;

// Every golden below is printed by an oracle, never copied from the API:
//   [PS]  python tools/profile_seed.py --dedupe          (counts, window, dedupe)
//   [OP]  python tools/oracle_poisson.py                 (λ, bands, verdicts, raw vs deduped)
//   [OPw] python tools/oracle_poisson.py --account A --week W   (one account-week, ranked)
// tools/verify_aggregates.py runs the same cases against a live API with the oracle in-process.
[Collection(SqlServerCollection.Name)]
public class PulseEndpointTests(PulseApiFixture api)
{
    private const string LastCompleteWeek = "2026-07-20"; // [PS] last complete bucket, every account

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Location(JsonElement pulse, string name) =>
        pulse.GetProperty("locations").EnumerateArray().Single(r => r.GetProperty("location").GetString() == name);

    private static string Verdict(JsonElement row) => row.GetProperty("verdict").GetString()!;

    private static int Count(JsonElement row) => row.GetProperty("count").GetInt32();

    private static double Lambda(JsonElement row) => row.GetProperty("lambda").GetDouble();

    private static bool Flagged(JsonElement row) => Verdict(row) is "above" or "below";

    // Rule: all reporting reads go through activity_events_dedup, never the raw table.
    // [OP] "account 6 Site E week 2026-02-23: raw 3 → deduped 2"
    [Fact]
    public async Task Dedupe_ReadsViewNotRawTable()
    {
        var pulse = await api.GetOkPulseAsync(6, "2026-02-23", Ct);

        Assert.Equal(2, Count(Location(pulse, "Site E")));
    }

    // Rule: default view is the last complete week, derived from the data, bucketed in account-local time.
    // [PS]/[OP] "Default week, per account"
    [Theory]
    [InlineData(1, 53)]
    [InlineData(6, 87)]
    [InlineData(8, 7)]
    [InlineData(18, 20)]
    [InlineData(19, 13)]
    public async Task Totals_LastCompleteWeek(int accountId, int expected)
    {
        var pulse = await api.GetOkPulseAsync(accountId, null, Ct);

        Assert.Equal(LastCompleteWeek, pulse.GetProperty("period").GetProperty("weekStart").GetString());
        Assert.True(pulse.GetProperty("period").GetProperty("isDefault").GetBoolean());
        Assert.Equal(expected, Count(pulse.GetProperty("account")));
    }

    // Rule: location key is (account_id, location); per-location counts sum the same deduped events.
    // [OPw] --account 1 (default week)
    [Fact]
    public async Task PerLocation_Account1()
    {
        var pulse = await api.GetOkPulseAsync(1, null, Ct);

        var counts = pulse.GetProperty("locations").EnumerateArray()
            .ToDictionary(r => r.GetProperty("location").GetString()!, Count);
        Assert.Equal(
            new Dictionary<string, int> { ["Site A"] = 9, ["Site B"] = 10, ["Site C"] = 9, ["Site D"] = 9, ["Site E"] = 9, ["Site F"] = 7 },
            counts);
    }

    // Rule: verdicts are exact Poisson tail tests against the trimmed baseline.
    // [OP] "account 6 week 2026-06-01: n=880 λ=72.08 … above (15/15 locations above)"
    [Fact]
    public async Task Spike_DetectedInOwnWeek()
    {
        var pulse = await api.GetOkPulseAsync(6, "2026-06-01", Ct);

        var account = pulse.GetProperty("account");
        Assert.Equal(880, Count(account));
        Assert.Equal(72.1, Lambda(account), 1);
        Assert.Equal("above", Verdict(account));
        var locations = pulse.GetProperty("locations").EnumerateArray().ToList();
        Assert.Equal(15, locations.Count);
        Assert.All(locations, r => Assert.Equal("above", Verdict(r)));
    }

    // Rule: the baseline drops one highest and one lowest week, so the spike cannot inflate λ.
    // [OP] "account 6, 12 weeks …: trimmed λ = 72.3 (plain mean 138.0)"; verdict from [OPw] --week 2026-06-08
    [Fact]
    public async Task Spike_DoesNotPoisonNextWeek()
    {
        var pulse = await api.GetOkPulseAsync(6, "2026-06-08", Ct);

        var account = pulse.GetProperty("account");
        Assert.Equal(102, Count(account));
        Assert.Equal(72.3, Lambda(account), 1);
        Assert.Equal("above", Verdict(account));
    }

    // Rule: a location with no events in a complete week is a zero, judged like any other count.
    // [OPw] --account 6 --week 2026-04-13: "Site G n=0 λ=5.25 … below"
    [Fact]
    public async Task ZeroFill_SilentLocationWeekAppears()
    {
        var pulse = await api.GetOkPulseAsync(6, "2026-04-13", Ct);

        var g = Location(pulse, "Site G");
        Assert.Equal(0, Count(g));
        Assert.Equal(5.25, Lambda(g), 10);
        Assert.Equal("below", Verdict(g));
    }

    // Rule: a complete week with no events counts as 0 in the baseline, not as missing.
    // Site G's window 2026-04-06 … 2026-06-22 is [2,0,2,6,4,3,7,5,57,4,5,3]; the trim drops 0 and 57 → 41/10.
    // Skipping the empty week would give 4.33 (fixed window) or 4.7 (window slides back to 2026-03-30).
    // [OPw] --account 6 --week 2026-06-29: "Site G n=0 λ=4.1 band=[1,10] below"
    [Fact]
    public async Task ZeroFill_EmptyWeekCountsAsZeroInBaseline()
    {
        var pulse = await api.GetOkPulseAsync(6, "2026-06-29", Ct);

        var g = Location(pulse, "Site G");
        Assert.Equal(0, Count(g));
        Assert.Equal(4.1, Lambda(g), 10);
        Assert.Equal(12, g.GetProperty("baselineWeeks").GetInt32());
        Assert.Equal("below", Verdict(g));
    }

    // Rule: baseline needs at least 8 prior complete weeks; 2026-03-23 has 7 (2026-02-02 … 2026-03-16).
    // [OPw] --account 18 --week 2026-03-23: "λ=- band=- insufficient_history"
    [Fact]
    public async Task Baseline_InsufficientHistory()
    {
        var pulse = await api.GetOkPulseAsync(18, "2026-03-23", Ct);

        var account = pulse.GetProperty("account");
        Assert.Equal("insufficient_history", Verdict(account));
        Assert.Equal(7, account.GetProperty("baselineWeeks").GetInt32());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("lambda").ValueKind);
        Assert.All(pulse.GetProperty("locations").EnumerateArray(), r => Assert.Equal("insufficient_history", Verdict(r)));
    }

    // Rule: rank by min(p_low/0.025, p_high/0.005) ascending — below 1 exactly when flagged.
    [Theory]
    [InlineData(15, null)]
    [InlineData(6, "2026-04-13")]
    [InlineData(6, "2026-06-08")]
    public async Task Ranking_FlaggedAboveUnflagged(int accountId, string? week)
    {
        var pulse = await api.GetOkPulseAsync(accountId, week, Ct);

        var rows = pulse.GetProperty("locations").EnumerateArray().ToList();
        var firstUnflagged = rows.FindIndex(r => !Flagged(r));
        Assert.True(firstUnflagged >= 0 || rows.All(Flagged));
        Assert.DoesNotContain(rows.Skip(firstUnflagged < 0 ? rows.Count : firstUnflagged), Flagged);

        // The wire carries p_low/p_high, so the order itself is checkable, not just the partition.
        var keys = rows.Select(r => Verdict(r) is "normal" or "above" or "below"
            ? Math.Min(r.GetProperty("pLow").GetDouble() / 0.025, r.GetProperty("pHigh").GetDouble() / 0.005)
            : double.PositiveInfinity).ToList();
        Assert.Equal(keys.Order(), keys);
    }

    // Unflagged rows still rank by closeness to a flag: Site B (2 vs λ 6.8, key 1.378) leads Site C
    // (12 vs λ 5.3, key 1.682) although neither is flagged.
    // [OPw] --account 15: "#1 Site B … #2 Site C … #3 Site A"
    [Fact]
    public async Task Ranking_Account15DefaultWeek_OrderMatchesOracle()
    {
        var pulse = await api.GetOkPulseAsync(15, null, Ct);

        var order = pulse.GetProperty("locations").EnumerateArray().Select(r => r.GetProperty("location").GetString()).ToList();
        Assert.Equal(["Site B", "Site C", "Site A"], order);
    }

    // Rule: an empty account is 200 with a populated window and insufficient_history — never 404.
    // [OP] "20: n=0 λ=- band=- insufficient_history"
    [Fact]
    public async Task Contract_EmptyAccount_Returns200InsufficientHistory()
    {
        var (status, pulse) = await api.GetPulseAsync(20, null, Ct);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("empty", pulse.GetProperty("state").GetString());
        Assert.Equal(LastCompleteWeek, pulse.GetProperty("period").GetProperty("weekStart").GetString());
        Assert.Equal(0, Count(pulse.GetProperty("account")));
        Assert.Equal("insufficient_history", Verdict(pulse.GetProperty("account")));
        Assert.Empty(pulse.GetProperty("locations").EnumerateArray());
    }

    // Rule: an account that does not exist is 404 with a machine-readable ProblemDetails title.
    [Fact]
    public async Task Contract_UnknownAccount_Returns404()
    {
        var (status, problem) = await api.GetPulseAsync(99, null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("pulse.account_not_found", problem.GetProperty("title").GetString());
    }

    // Rule: partial weeks are never served ([PS] 2026-07-27 covers 1/7 days); only complete-week Mondays are.
    [Theory]
    [InlineData("2026-07-27")] // partial week
    [InlineData("2026-07-22")] // Wednesday inside a complete week
    [InlineData("2026-7-20")]  // not YYYY-MM-DD
    public async Task Contract_WeekNotACompleteMonday_Returns400(string week)
    {
        var (status, problem) = await api.GetPulseAsync(1, week, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("pulse.invalid_week", problem.GetProperty("title").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
    }

    // Rule: verdicts go over the wire as snake_case strings; a volume-gated row has no band.
    // [OPw] --account 6 --week 2026-04-13: "Site F n=6 λ=3.625 band=- not_enough_volume", plus above/below/normal rows.
    [Fact]
    public async Task Contract_Verdicts_SerializeAsSnakeCaseStrings_BandNullWhenNotEnoughVolume()
    {
        var pulse = await api.GetOkPulseAsync(6, "2026-04-13", Ct);

        var rows = pulse.GetProperty("locations").EnumerateArray().Append(pulse.GetProperty("account")).ToList();
        Assert.All(rows, r =>
        {
            Assert.Equal(JsonValueKind.String, r.GetProperty("verdict").ValueKind);
            Assert.Contains(Verdict(r), (string[])["normal", "above", "below", "not_enough_volume", "insufficient_history"]);
        });
        var gated = rows.Where(r => Verdict(r) == "not_enough_volume").ToList();
        Assert.NotEmpty(gated);
        Assert.All(gated, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("band").ValueKind));
        Assert.All(rows.Where(r => Verdict(r) is "normal" or "above" or "below"), r =>
        {
            var band = r.GetProperty("band");
            Assert.True(band.GetProperty("lo").GetInt32() <= band.GetProperty("hi").GetInt32());
        });
    }
}
