# Transcript agent-ad84570dbdcf7e244

Source: raw/agent-ad84570dbdcf7e244.jsonl (unedited)

### 🧑 Me (subagent) · 2026-09-28T12:19:58.043Z

Review everything changed in C:\demos\relay-pulse since the T1 commit (90f63c2). You have no git access, so here is the complete change set.

**New files (untracked; read them in full):**
- src/RelayPulse.Core/Statistics/PoissonTail.cs, Baseline.cs, Significance.cs, Assessment.cs, CountBand.cs, Verdict.cs
- src/RelayPulse.Core/Pulse/PulseBuilder.cs, PulseReport.cs
- src/RelayPulse.Api/Pulse/PulseQuery.cs (the raw GROUPING SETS aggregation SQL), PulseEndpoints.cs, PulseResponse.cs
- tests/RelayPulse.Tests/Statistics/PoissonTailTests.cs, BaselineTests.cs, BandTests.cs, AssessTests.cs
- tests/RelayPulse.Tests/Pulse/PulseBuilderTests.cs

**Modified tracked files:**
- src/RelayPulse.Api/Program.cs: registers PulseQuery (scoped), adds JsonStringEnumConverter(SnakeCaseLower) via ConfigureHttpJsonOptions, and calls app.MapPulse().
- CLAUDE.md: the Statistics rules, the account-6 edge case, the golden table and the Status section were rewritten. Read the current file. The rules that were replaced:
  - OLD: "quasi-Poisson band λ ± 2.576·√(φλ), φ clamped [1,4]"
  - OLD: "Baseline = trailing 12 complete weeks, min 8, excluding detected anomaly weeks"
  - OLD: "Below λ = 3/week report not enough volume"
  - OLD goldens: acct 6 baseline "72.7 excl / 105.0 incl"; acct 8 "7 vs λ=10.4"
- ai-log/decisions.md: new entries appended at the end, timestamped 2026-09-28 07:16.
- .claude/agents/aggregation-reviewer.md: your own definition, added in commit f999b32. Skip it.

**Spec the user gave for this work (the authority where it conflicts with PLAN.md):**
- PoissonTail: p_low = P(X≤n), p_high = P(X≥n) = 1−P(X≤n−1). Built from iterative terms or log-space, no factorials, stable up to λ=500.
- Baseline: up to 12 prior complete weeks, minimum 8 (otherwise InsufficientHistory). Drop one highest and one lowest, take the mean of the rest.
- Band: the integer range [lo, hi] of counts that would not be flagged. α_low=0.025, α_high=0.005.
- Verdict: NotEnoughVolume if λ<3.7; Below if p_low<α_low; Above if p_high<α_high; else Normal.
- RankKey = min(p_low/α_low, p_high/α_high).
- Endpoint: GET /api/accounts/{id}/pulse?week=YYYY-MM-DD. Raw parameterised SqlQueryRaw over the dedupe view joined to week_buckets.
- Zero-fill in C#: every (location, complete week) in the observed window gets a row, n=0 if it has no events, and nothing before the first complete week.
- The default week is the last complete week from the GLOBAL cutoff. The week must be the Monday of a complete bucket, otherwise 400. An unknown account is 404. An account with no events is 200 with an empty state.
- Response: the period, an account row, and location rows ranked by RankKey. Each row has count, λ, band, p_low, p_high, verdict, and a per-type split with NULL outcome shown as "unspecified".

**Already verified, so you don't need to re-derive it:** 80/80 tests pass. I checked the live responses against an independent Python recomputation over the dedupe view: every count and λ matches for acct 1 week 2026-07-20 (53; A=9 B=10 C=9 D=9 E=9 F=7) and acct 6 week 2026-06-01 (880, above).

**What I want from you:** correctness findings, ranked most severe first, each with file:line and a concrete failing scenario (input or state → wrong output). Look hardest at:
1. Numerical correctness and termination of PoissonTail, and of the BandFor binary-search bounds, across λ ∈ [0, 500+] and large n.
2. Whether BandFor and Assess can ever disagree.
3. Whether the SQL can mis-bucket, double-count across the two GROUPING SETS, or read the raw table.
4. Zero-fill and window edge cases: target equal to the first complete week, locations missing from the 13-week fetch window, rows outside the window.
5. Consistency with the CLAUDE.md domain rules (timezone, dedupe, partial weeks, no wall clock, DateTime in raw-SQL rows, CancellationToken, ProblemDetails codes, logging templates).
6. Test weaknesses: tests that would still pass if the code were wrong.

Separate confirmed defects from judgement calls. Don't pad the list.

### 🧑 Me (subagent) · 2026-09-28T12:19:58.050Z

<system-reminder>
Your final report is delivered through SubagentHandback: when your work is complete, call SubagentHandback({message: <your full report>}) and then stop. Only a SubagentHandback call reaches your caller as your result; plain text you write at the end is not delivered.
</system-reminder>

### 🤖 Agent (subagent) · 2026-09-28T12:20:00.229Z

I'll read all the changed files.

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:00.230Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Core\\Statistics\\PoissonTail.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:00.715Z

```
1	namespace RelayPulse.Core.Statistics;
2	
3	/// <summary>
4	/// Exact Poisson tail probabilities. Terms are built by the recurrence
5	/// ln p(k) = ln p(k−1) + ln λ − ln k and summed relative to the largest term, so there are no
6	/// factorials and nothing overflows or underflows prematurely (λ = 500 and beyond).
7	/// </summary>
8	public static class PoissonTail
9	{
10	    // Stop summing the upper tail once a term adds less than this, relative to the sum so far.
11	    private const double Epsilon = 1e-17;
12	
13	    /// <summary>P(X ≤ n).</summary>
14	    public static double Lower(int n, double lambda)
15	    {
16	        EnsureValid(lambda);
17	        if (n < 0)
18	        {
19	            return 0.0;
20	        }
21	
22	        if (lambda == 0.0)
23	        {
24	            return 1.0;
25	        }
26	
27	        // Every term is positive, so a direct sum is stable; scale by the largest (the mode, or
28	        // the last term when n sits below the mode) to keep exp() in range.
29	        var logLambda = Math.Log(lambda);
30	        var peak = LogPmf(Math.Min(n, (int)Math.Floor(lambda)), lambda, logLambda);
31	        var logTerm = -lambda;
32	        var sum = 0.0;
33	        for (var k = 0; k <= n; k++)
34	        {
35	            if (k > 0)
36	            {
37	                logTerm += logLambda - Math.Log(k);
38	            }
39	
40	            sum += Math.Exp(logTerm - peak);
41	        }
42	
43	        return Math.Min(1.0, Math.Exp(peak) * sum);
44	    }
45	
46	    /// <summary>P(X ≥ n) = 1 − P(X ≤ n−1).</summary>
47	    public static double Upper(int n, double lambda)
48	    {
49	        EnsureValid(lambda);
50	        if (n <= 0)
51	        {
52	            return 1.0;
53	        }
54	
55	        if (lambda == 0.0)
56	        {
57	            return 0.0;
58	        }
59	
60	        // At or below the mean the upper tail is large and the complement loses nothing.
61	        if (n <= lambda)
62	        {
63	            return 1.0 - Lower(n - 1, lambda);
64	        }
65	
66	        // Past the mean, 1 − P(X ≤ n−1) cancels catastrophically; sum the tail directly. Terms
67	        // shrink monotonically for k > λ, so stop once they stop mattering.
68	        var first = LogPmf(n, lambda, Math.Log(lambda));
69	        var term = 1.0;
70	        var sum = 0.0;
71	        for (var k = n; term > Epsilon * sum; k++)
72	        {
73	            sum += term;
74	            term *= lambda / (k + 1);
75	        }
76	
77	        return Math.Exp(first) * sum;
78	    }
79	
80	    private static double LogPmf(int k, double lambda, double logLambda)
81	    {
82	        var log = -lambda;
83	        for (var j = 1; j <= k; j++)
84	        {
85	            log += logLambda - Math.Log(j);
86	        }
87	
88	        return log;
89	    }
90	
91	    private static void EnsureValid(double lambda)
92	    {
93	        if (!double.IsFinite(lambda) || lambda < 0.0)
94	        {
95	            throw new ArgumentOutOfRangeException(nameof(lambda), lambda, "
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:00.766Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Core\\Statistics\\Baseline.cs"
}
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:00.866Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Core\\Statistics\\Significance.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:01.289Z

```
1	namespace RelayPulse.Core.Statistics;
2	
3	/// <summary>
4	/// Exact Poisson tail tests against the baseline λ. Asymmetric on purpose: the low tail
5	/// (α = 0.025) is 5× more sensitive than the high tail (α = 0.005).
6	/// </summary>
7	public static class Significance
8	{
9	    public const double AlphaLow = 0.025;
10	    public const double AlphaHigh = 0.005;
11	
12	    /// <summary>
13	    /// Volume gate. Just above ln(1/α_low) = 3.689, the λ at which an empty week first becomes
14	    /// detectable as Below.
15	    /// </summary>
16	    public const double MinLambda = 3.7;
17	
18	    /// <summary>The integer counts <see cref="Assess"/> would call Normal, ignoring the volume gate.</summary>
19	    public static CountBand BandFor(double lambda)
20	    {
21	        // Lower(n) rises with n and Upper(n) falls, so both edges are binary searches.
22	        // lo: smallest n with P(X ≤ n) ≥ α_low. P(X ≤ ⌈λ⌉) ≥ ~½, so it is never above ⌈λ⌉.
23	        var lo = FirstWhere(0, (int)Math.Ceiling(lambda), n => PoissonTail.Lower(n, lambda) >= AlphaLow);
24	
25	        // hi: one below the smallest n with P(X ≥ n) < α_high. λ + 10√λ + 20 is far past it.
26	        var ceiling = (int)Math.Ceiling(lambda + 10 * Math.Sqrt(lambda) + 20);
27	        var hi = FirstWhere(1, ceiling, n => PoissonTail.Upper(n, lambda) < AlphaHigh) - 1;
28	
29	        return new CountBand(lo, hi);
30	    }
31	
32	    public static Assessment Assess(int count, double? lambda)
33	    {
34	        ArgumentOutOfRangeException.ThrowIfNegative(count);
35	        if (lambda is not { } l)
36	        {
37	            return new Assessment(Verdict.InsufficientHistory, double.PositiveInfinity);
38	        }
39	
40	        var pLow = PoissonTail.Lower(count, l);
41	        var pHigh = PoissonTail.Upper(count, l);
42	        var band = BandFor(l);
43	
44	        if (l < MinLambda)
45	        {
46	            return new Assessment(Verdict.NotEnoughVolume, double.PositiveInfinity, l, band, pLow, pHigh);
47	        }
48	
49	        var verdict = pLow < AlphaLow ? Verdict.Below
50	            : pHigh < AlphaHigh ? Verdict.Above
51	            : Verdict.Normal;
52	
53	        return new Assessment(verdict, Math.Min(pLow / AlphaLow, pHigh / AlphaHigh), l, band, pLow, pHigh);
54	    }
55	
56	    // Smallest n in [from, to] for a monotone predicate (false…false, true…true); `to` if none holds.
57	    private static int FirstWhere(int from, int to, Func<int, bool> predicate)
58	    {
59	        while (from < to)
60	        {
61	            var mid = from + (to - from) / 2;
62	            if (predicate(mid))
63	            {
64	                to = mid;
65	            }
66	            else
67	            {
68	                from = mid + 1;
69	            }
70	        }
71	
72	        return from;
73	    }
74	}
75	
```

### Tool result (subagent) · 2026-09-28T12:20:01.294Z

```
1	namespace RelayPulse.Core.Statistics;
2	
3	/// <summary>
4	/// Expected weekly rate λ from a series' own history: the trailing <see cref="Window"/> complete
5	/// weeks, one highest and one lowest dropped, mean of the rest. The trim is what keeps a single
6	/// anomaly week (acct 6, 2026-06-01) from inflating the baselines that follow it.
7	/// </summary>
8	public static class Baseline
9	{
10	    public const int Window = 12;
11	    public const int MinWeeks = 8;
12	
13	    /// <param name="priorCompleteWeeks">
14	    /// Counts for complete weeks strictly before the week being judged, oldest first. The caller
15	    /// excludes partial weeks; only the last <see cref="Window"/> are used.
16	    /// </param>
17	    /// <returns>λ, or <c>null</c> when fewer than <see cref="MinWeeks"/> weeks are available.</returns>
18	    public static double? Lambda(IReadOnlyList<int> priorCompleteWeeks)
19	    {
20	        ArgumentNullException.ThrowIfNull(priorCompleteWeeks);
21	        if (priorCompleteWeeks.Any(c => c < 0))
22	        {
23	            throw new ArgumentOutOfRangeException(nameof(priorCompleteWeeks), "Weekly counts cannot be negative.");
24	        }
25	
26	        if (priorCompleteWeeks.Count < MinWeeks)
27	        {
28	            return null;
29	        }
30	
31	        var sorted = priorCompleteWeeks
32	            .Skip(Math.Max(0, priorCompleteWeeks.Count - Window))
33	            .Order()
34	            .ToArray();
35	
36	        return sorted[1..^1].Average();
37	    }
38	}
39	
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:01.366Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Core\\Statistics\\Assessment.cs"
}
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:01.866Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Core\\Statistics\\CountBand.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:01.901Z

```
1	namespace RelayPulse.Core.Statistics;
2	
3	/// <param name="RankKey">
4	/// min(p_low/α_low, p_high/α_high). Below 1 exactly when flagged, so ascending order puts every
5	/// flagged row first, most extreme at the top. +∞ when no verdict was reached.
6	/// </param>
7	/// <param name="Lambda">Expected count; null when history is insufficient.</param>
8	/// <param name="Band">Counts that would be Normal at this λ; null when λ is null.</param>
9	/// <param name="PLow">P(X ≤ count); null when λ is null.</param>
10	/// <param name="PHigh">P(X ≥ count); null when λ is null.</param>
11	public sealed record Assessment(
12	    Verdict Verdict,
13	    double RankKey,
14	    double? Lambda = null,
15	    CountBand? Band = null,
16	    double? PLow = null,
17	    double? PHigh = null);
18	
```

### Tool result (subagent) · 2026-09-28T12:20:02.352Z

```
1	namespace RelayPulse.Core.Statistics;
2	
3	/// <summary>Inclusive range of weekly counts that would be judged normal for a given λ.</summary>
4	public sealed record CountBand(int Lo, int Hi);
5	
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:02.820Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Core\\Statistics\\Verdict.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:03.260Z

```
1	namespace RelayPulse.Core.Statistics;
2	
3	public enum Verdict
4	{
5	    Normal,
6	    Above,
7	    Below,
8	
9	    /// <summary>λ below <see cref="Significance.MinLambda"/>: only large swings would be detectable.</summary>
10	    NotEnoughVolume,
11	
12	    /// <summary>Fewer than <see cref="Baseline.MinWeeks"/> prior complete weeks.</summary>
13	    InsufficientHistory,
14	}
15	
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:05.367Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Core\\Pulse\\PulseBuilder.cs"
}
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:05.375Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Core\\Pulse\\PulseReport.cs"
}
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:05.924Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Api\\Pulse\\PulseQuery.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:05.961Z

```
1	using RelayPulse.Core.Statistics;
2	
3	namespace RelayPulse.Core.Pulse;
4	
5	/// <summary>
6	/// One aggregation row: events in one account-local week, at one grain, split by type and outcome.
7	/// <see cref="Location"/> is null on account-grain rows (the second GROUPING SETS set); the column
8	/// is NOT NULL in the schema, so null is unambiguous.
9	/// </summary>
10	public sealed record WeeklyCount(DateOnly WeekStart, string? Location, string EventType, string? Outcome, int Events);
11	
12	/// <param name="HasEvents">False when the account has no events in any complete week up to the target.</param>
13	public sealed record PulseReport(bool HasEvents, SeriesPulse Account, IReadOnlyList<SeriesPulse> Locations);
14	
15	/// <summary>The judged week for one series: the whole account (<see cref="Location"/> null) or one location.</summary>
16	/// <param name="BaselineWeeks">Prior complete weeks the baseline drew on (at most 12, before trimming).</param>
17	public sealed record SeriesPulse(
18	    string? Location,
19	    int Count,
20	    int BaselineWeeks,
21	    Assessment Assessment,
22	    IReadOnlyList<TypeSplit> ByType);
23	
24	/// <param name="Outcomes">Per-type outcome counts; a NULL outcome is keyed <see cref="PulseBuilder.UnspecifiedOutcome"/>.</param>
25	public sealed record TypeSplit(string EventType, int Count, IReadOnlyDictionary<string, int> Outcomes);
26	
```

### Tool result (subagent) · 2026-09-28T12:20:05.973Z

```
1	using RelayPulse.Core.Statistics;
2	
3	namespace RelayPulse.Core.Pulse;
4	
5	/// <summary>
6	/// Turns aggregation rows into judged series for one account-week. Pure: no clock, no I/O.
7	/// </summary>
8	public static class PulseBuilder
9	{
10	    public const string UnspecifiedOutcome = "unspecified";
11	
12	    /// <param name="completeWeeks">
13	    /// The account timezone's complete week Mondays, ascending. These define the zero-fill: every
14	    /// series gets a count for each of them, and nothing outside them is ever read.
15	    /// </param>
16	    /// <param name="week">The week being judged; must be one of <paramref name="completeWeeks"/>.</param>
17	    /// <param name="rows">Both grains from the aggregation query; rows outside the window are ignored.</param>
18	    public static PulseReport Build(IReadOnlyList<DateOnly> completeWeeks, DateOnly week, IReadOnlyList<WeeklyCount> rows)
19	    {
20	        ArgumentNullException.ThrowIfNull(completeWeeks);
21	        ArgumentNullException.ThrowIfNull(rows);
22	
23	        var targetIndex = IndexOf(completeWeeks, week);
24	        if (targetIndex < 0)
25	        {
26	            throw new ArgumentException($"{week:yyyy-MM-dd} is not a complete week.", nameof(week));
27	        }
28	
29	        // Up to and including the target; weeks after it and partial weeks never enter.
30	        var inWindow = completeWeeks.Take(targetIndex + 1).ToHashSet();
31	        var relevant = rows.Where(r => inWindow.Contains(r.WeekStart)).ToList();
32	        var prior = completeWeeks.Take(targetIndex).ToList();
33	
34	        if (relevant.Count == 0)
35	        {
36	            // No events at all is no history, not a zero-volume history.
37	            var empty = new SeriesPulse(null, 0, Math.Min(prior.Count, Baseline.Window), Significance.Assess(0, null), []);
38	            return new PulseReport(false, empty, []);
39	        }
40	
41	        var account = Judge(null, relevant.Where(r => r.Location is null).ToList(), prior, week);
42	        var locations = relevant
43	            .Where(r => r.Location is not null)
44	            .GroupBy(r => r.Location!, StringComparer.Ordinal)
45	            .Select(g => Judge(g.Key, g.ToList(), prior, week))
46	            .OrderBy(s => s.Assessment.RankKey)
47	            .ThenBy(s => s.Location, StringComparer.Ordinal)
48	            .ToList();
49	
50	        return new PulseReport(true, account, locations);
51	    }
52	
53	    private static SeriesPulse Judge(string? location, List<WeeklyCount> rows, List<DateOnly> prior, DateOnly week)
54	    {
55	        var perWeek = rows
56	            .GroupBy(r => r.WeekStart)
57	            .ToDictionary(g => g.Key, g => g.Sum(r => r.Events));
58	
59	        var history = prior.Select(w => perWeek.GetValueOrDefault(w)).ToList();
60	        var count = perWeek.GetValueOrDefault(week);
61	        var byType = rows
62	            .Where(r => r.WeekStart == week)
63	            .GroupBy(r => r.EventType, StringComparer.Ordin
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:06.475Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Api\\Pulse\\PulseEndpoints.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:06.563Z

```
1	using System.Data;
2	using Microsoft.Data.SqlClient;
3	using Microsoft.EntityFrameworkCore;
4	using RelayPulse.Api.Data;
5	using RelayPulse.Core.Pulse;
6	
7	namespace RelayPulse.Api.Pulse;
8	
9	/// <summary>The aggregation: weekly counts for one account at both grains, in one statement.</summary>
10	public sealed class PulseQuery(RelayDbContext db)
11	{
12	    // Events are bucketed by a range join onto the account timezone's precomputed weeks, so
13	    // bucketing is account-local and DST-correct, and partial weeks are excluded by a column
14	    // rather than by date arithmetic. Reads the dedupe view, never the raw table.
15	    //
16	    // GROUPING SETS: the first set is the location grain, the second the account grain (its
17	    // location comes back NULL, which the NOT NULL column can never produce by itself).
18	    private const string Sql = $"""
19	        SELECT b.week_start_local AS WeekStart,
20	               e.location         AS Location,
21	               e.event_type       AS EventType,
22	               e.outcome          AS Outcome,
23	               COUNT(*)           AS Events
24	        FROM accounts a
25	        JOIN week_buckets b
26	          ON b.timezone = a.timezone
27	         AND b.is_complete = 1
28	         AND b.week_start_local >= @from
29	         AND b.week_start_local <= @to
30	        JOIN {RelayDbContext.DedupView} e
31	          ON e.account_id = a.id
32	         AND e.occurred_at >= b.utc_start
33	         AND e.occurred_at <  b.utc_end
34	        WHERE a.id = @accountId
35	        GROUP BY GROUPING SETS (
36	            (b.week_start_local, e.location, e.event_type, e.outcome),
37	            (b.week_start_local, e.event_type, e.outcome)
38	        )
39	        """;
40	
41	    public Task<List<WeeklyCount>> RunAsync(int accountId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
42	        db.Database
43	            .SqlQueryRaw<WeeklyCount>(
44	                Sql,
45	                new SqlParameter("@accountId", SqlDbType.Int) { Value = accountId },
46	                new SqlParameter("@from", SqlDbType.Date) { Value = from },
47	                new SqlParameter("@to", SqlDbType.Date) { Value = to })
48	            .ToListAsync(cancellationToken);
49	}
50	
```

### Tool result (subagent) · 2026-09-28T12:20:07.013Z

```
1	using System.Globalization;
2	using Microsoft.AspNetCore.Http.HttpResults;
3	using Microsoft.EntityFrameworkCore;
4	using RelayPulse.Api.Data;
5	using RelayPulse.Core.Pulse;
6	using RelayPulse.Core.Statistics;
7	
8	namespace RelayPulse.Api.Pulse;
9	
10	public static class PulseEndpoints
11	{
12	    public const string InvalidWeek = "pulse.invalid_week";
13	    public const string AccountNotFound = "pulse.account_not_found";
14	
15	    public static IEndpointRouteBuilder MapPulse(this IEndpointRouteBuilder app)
16	    {
17	        app.MapGet("/api/accounts/{id:int}/pulse", GetPulseAsync);
18	        return app;
19	    }
20	
21	    private static async Task<Results<Ok<PulseResponse>, ProblemHttpResult>> GetPulseAsync(
22	        int id,
23	        string? week,
24	        RelayDbContext db,
25	        PulseQuery query,
26	        ILoggerFactory loggerFactory,
27	        CancellationToken cancellationToken)
28	    {
29	        var logger = loggerFactory.CreateLogger(typeof(PulseEndpoints).FullName!);
30	        DateOnly? requested = null;
31	        if (week is not null)
32	        {
33	            if (!DateOnly.TryParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
34	            {
35	                return Problem(400, InvalidWeek, $"'{week}' is not a date in YYYY-MM-DD form.");
36	            }
37	
38	            requested = parsed;
39	        }
40	
41	        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
42	        if (account is null)
43	        {
44	            return Problem(404, AccountNotFound, $"No account with id {id}.");
45	        }
46	
47	        // Complete weeks come from week_buckets, which is built from the global MIN/MAX(occurred_at):
48	        // the default is the last complete week of the data, never of the wall clock.
49	        var completeWeeks = await db.WeekBuckets.AsNoTracking()
50	            .Where(b => b.Timezone == account.Timezone && b.IsComplete)
51	            .OrderBy(b => b.WeekStartLocal)
52	            .Select(b => b.WeekStartLocal)
53	            .ToListAsync(cancellationToken);
54	
55	        if (completeWeeks.Count == 0)
56	        {
57	            return Problem(400, InvalidWeek, "There are no complete weeks of data yet.");
58	        }
59	
60	        var target = requested ?? completeWeeks[^1];
61	        var index = completeWeeks.BinarySearch(target);
62	        if (index < 0)
63	        {
64	            return Problem(400, InvalidWeek,
65	                $"{target:yyyy-MM-dd} is not the Monday of a complete week; choose a Monday from " +
66	                $"{completeWeeks[0]:yyyy-MM-dd} to {completeWeeks[^1]:yyyy-MM-dd}.");
67	        }
68	
69	        // Only the baseline window and the target week are needed.
70	        var from = completeWeeks[Math.Max(0, index - Baseline.Window)];
71	        var rows = await query.RunAsync(id, from, target, cancellationToken);
72	        var report = PulseBuil
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:07.042Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Api\\Pulse\\PulseResponse.cs"
}
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:07.256Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\src\\RelayPulse.Api\\Program.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:07.527Z

```
1	using RelayPulse.Core.Pulse;
2	using RelayPulse.Core.Statistics;
3	
4	namespace RelayPulse.Api.Pulse;
5	
6	/// <param name="State"><c>ok</c>, or <c>empty</c> when the account has no events to judge.</param>
7	public sealed record PulseResponse(
8	    int AccountId,
9	    string AccountName,
10	    string Timezone,
11	    PulsePeriod Period,
12	    string State,
13	    PulseRow Account,
14	    IReadOnlyList<PulseRow> Locations);
15	
16	/// <param name="WeekEnd">Sunday, inclusive, in the account's timezone.</param>
17	/// <param name="PreviousWeek">Previous complete week, null at the start of the window.</param>
18	/// <param name="NextWeek">Next complete week, null at the last complete week.</param>
19	public sealed record PulsePeriod(
20	    DateOnly WeekStart,
21	    DateOnly WeekEnd,
22	    bool IsDefault,
23	    DateOnly FirstCompleteWeek,
24	    DateOnly LastCompleteWeek,
25	    DateOnly? PreviousWeek,
26	    DateOnly? NextWeek);
27	
28	/// <param name="Location">Null on the account row.</param>
29	/// <param name="Lambda">Expected count from the trimmed 12-week baseline; null with insufficient history.</param>
30	/// <param name="PLow">P(X ≤ count).</param>
31	/// <param name="PHigh">P(X ≥ count).</param>
32	public sealed record PulseRow(
33	    string? Location,
34	    int Count,
35	    Verdict Verdict,
36	    double? Lambda,
37	    CountBand? Band,
38	    double? PLow,
39	    double? PHigh,
40	    int BaselineWeeks,
41	    IReadOnlyList<TypeSplit> ByType)
42	{
43	    public static PulseRow From(SeriesPulse s) => new(
44	        s.Location,
45	        s.Count,
46	        s.Assessment.Verdict,
47	        s.Assessment.Lambda,
48	        s.Assessment.Band,
49	        s.Assessment.PLow,
50	        s.Assessment.PHigh,
51	        s.BaselineWeeks,
52	        s.ByType);
53	}
54	
```

### Tool result (subagent) · 2026-09-28T12:20:07.769Z

```
1	using System.Text.Json;
2	using System.Text.Json.Serialization;
3	using Microsoft.EntityFrameworkCore;
4	using RelayPulse.Api.Data;
5	using RelayPulse.Api.Data.Seeding;
6	using RelayPulse.Api.Pulse;
7	using RelayPulse.Core;
8	
9	var builder = WebApplication.CreateBuilder(args);
10	
11	// Supplied by appsettings.Development.json locally, or ConnectionStrings__Relay in any environment.
12	var connectionString = builder.Configuration.GetConnectionString("Relay")
13	    ?? throw new InvalidOperationException(
14	        "Connection string 'Relay' is not configured. Set ConnectionStrings__Relay (see .env.example).");
15	
16	builder.Services.AddCore();
17	builder.Services.AddDbContext<RelayDbContext>(options => options.UseSqlServer(connectionString));
18	builder.Services.AddScoped<SeedImporter>();
19	builder.Services.AddScoped<PulseQuery>();
20	builder.Services.AddProblemDetails();
21	
22	// Verdicts go over the wire as "not_enough_volume", "insufficient_history", …
23	builder.Services.ConfigureHttpJsonOptions(options =>
24	    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));
25	
26	var app = builder.Build();
27	
28	// `dotnet run --project src/RelayPulse.Api -- seed [path]`: migrate, load the fixture, build week_buckets, exit.
29	if (args is ["seed", .. var rest])
30	{
31	    await using var scope = app.Services.CreateAsyncScope();
32	    var importer = scope.ServiceProvider.GetRequiredService<SeedImporter>();
33	    await importer.RunAsync(SeedPath.Resolve(rest is [var path, ..] ? path : null), CancellationToken.None);
34	    return;
35	}
36	
37	app.UseExceptionHandler();
38	app.UseStatusCodePages();
39	
40	app.MapPulse();
41	
42	app.Run();
43	
44	public partial class Program;
45	
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:10.337Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\tests\\RelayPulse.Tests\\Statistics\\PoissonTailTests.cs"
}
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:10.414Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\tests\\RelayPulse.Tests\\Statistics\\BaselineTests.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:10.905Z

```
1	using RelayPulse.Core.Statistics;
2	
3	namespace RelayPulse.Tests.Statistics;
4	
5	public class PoissonTailTests
6	{
7	    // Reference values: math.fsum of exp(k·ln λ − λ − lgamma(k+1)) in Python — a log-space
8	    // implementation independent of the C# one.
9	    private const double Rel = 1e-9;
10	
11	    private static void AssertRelative(double expected, double actual) =>
12	        Assert.True(Math.Abs(actual - expected) <= Rel * expected,
13	            $"expected {expected:R}, got {actual:R}");
14	
15	    [Theory]
16	    [InlineData(2, 3.0, 0.42319008112684364)]
17	    [InlineData(0, 3.68, 0.025222974835227212)]
18	    [InlineData(0, 3.69, 0.024972002042276155)]
19	    [InlineData(40, 75.0, 6.872731725629959e-06)]
20	    [InlineData(400, 500.0, 2.0780896100118295e-06)]
21	    public void Lower_ReferenceValues_MatchIndependentImplementation(int n, double lambda, double expected)
22	    {
23	        AssertRelative(expected, PoissonTail.Lower(n, lambda));
24	    }
25	
26	    [Theory]
27	    [InlineData(9, 3.0, 0.0038029920616759662)]
28	    [InlineData(14, 6.0, 0.003628492738722768)]
29	    [InlineData(600, 500.0, 7.785272561879036e-06)]
30	    public void Upper_ReferenceValues_MatchIndependentImplementation(int n, double lambda, double expected)
31	    {
32	        // Far upper tail: 1 − P(X≤n−1) would lose most digits to cancellation here.
33	        AssertRelative(expected, PoissonTail.Upper(n, lambda));
34	    }
35	
36	    [Fact]
37	    public void Lower_ZeroEventsAtLn40_CrossesAlphaLowBetween368And369()
38	    {
39	        // n=0 is Below iff e^−λ < 0.025 iff λ > ln 40 = 3.6889.
40	        Assert.True(PoissonTail.Lower(0, 3.68) >= 0.025);
41	        Assert.True(PoissonTail.Lower(0, 3.69) < 0.025);
42	    }
43	
44	    [Fact]
45	    public void Upper_Account6SpikeAgainstTrimmedBaseline_IsFiniteAndEffectivelyZero()
46	    {
47	        var p = PoissonTail.Upper(880, 72.3);
48	
49	        Assert.False(double.IsNaN(p));
50	        Assert.InRange(p, 0.0, 1e-300);
51	    }
52	
53	    [Theory]
54	    [InlineData(0.1)]
55	    [InlineData(3.0)]
56	    [InlineData(72.3)]
57	    [InlineData(500.0)]
58	    public void LowerAndUpper_AdjacentCounts_SumToOne(double lambda)
59	    {
60	        // P(X≤n) + P(X≥n+1) = 1 for every n: the two tails partition the distribution.
61	        for (var n = 0; n <= (int)(3 * lambda) + 10; n++)
62	        {
63	            Assert.Equal(1.0, PoissonTail.Lower(n, lambda) + PoissonTail.Upper(n + 1, lambda), 12);
64	        }
65	    }
66	
67	    [Fact]
68	    public void Tails_NonPositiveCounts_AreTrivial()
69	    {
70	        Assert.Equal(0.0, PoissonTail.Lower(-1, 5.0));
71	        Assert.Equal(1.0, PoissonTail.Upper(0, 5.0));
72	        Assert.Equal(1.0, PoissonTail.Upper(-3, 5.0));
73	    }
74	
75	    [Fact]
76	    public void Tails_ZeroLambda_IsPointMassAtZero()
77	    {
78	        // A baseline of all-zero weeks is legal input; it must not produce NaN.
79	        Assert.Equal(1.0, PoissonTail.Lower(0, 0.0));
80	
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:10.919Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\tests\\RelayPulse.Tests\\Statistics\\BandTests.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:10.974Z

```
1	using RelayPulse.Core.Statistics;
2	
3	namespace RelayPulse.Tests.Statistics;
4	
5	public class BaselineTests
6	{
7	    // Account 6, deduped, account-local weeks 2026-03-16 … 2026-06-01: the 12 complete weeks
8	    // before 2026-06-08 (tools/profile_seed.py --dedupe). The last is the 880 spike.
9	    private static readonly int[] Account6Before0608 = [81, 71, 82, 76, 55, 64, 86, 68, 78, 62, 53, 880];
10	
11	    [Fact]
12	    public void Lambda_Account6WeekAfterSpike_IsTrimmedMean723NotPlainMean138()
13	    {
14	        var lambda = Baseline.Lambda(Account6Before0608);
15	
16	        Assert.NotNull(lambda);
17	        Assert.Equal(72.3, lambda.Value, 10);
18	        Assert.Equal(138.0, Account6Before0608.Average(), 10); // guards the fixture itself
19	    }
20	
21	    [Fact]
22	    public void Lambda_SevenWeeks_IsInsufficientHistory()
23	    {
24	        Assert.Null(Baseline.Lambda([10, 11, 12, 13, 14, 15, 16]));
25	    }
26	
27	    [Fact]
28	    public void Lambda_NoWeeks_IsInsufficientHistory()
29	    {
30	        Assert.Null(Baseline.Lambda([]));
31	    }
32	
33	    [Fact]
34	    public void Lambda_EightWeeks_DropsExtremesAndAveragesSix()
35	    {
36	        // Drop 1 and 100 → mean of 10,10,10,20,20,20 = 15.
37	        Assert.Equal(15.0, Baseline.Lambda([10, 100, 20, 10, 1, 20, 10, 20])!.Value, 10);
38	    }
39	
40	    [Fact]
41	    public void Lambda_MoreThanTwelveWeeks_UsesOnlyTheTwelveMostRecent()
42	    {
43	        // Oldest-first input: the three leading 1000s are outside the trailing window.
44	        int[] weeks = [1000, 1000, 1000, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5];
45	
46	        Assert.Equal(5.0, Baseline.Lambda(weeks)!.Value, 10);
47	    }
48	
49	    [Fact]
50	    public void Lambda_TiedExtremes_DropsExactlyOneOfEach()
51	    {
52	        // Sorted: 2,2,4,4,4,4,8,8 → drop one 2 and one 8 → (2+4·4+8)/6.
53	        Assert.Equal(26.0 / 6, Baseline.Lambda([2, 8, 4, 2, 4, 8, 4, 4])!.Value, 10);
54	    }
55	
56	    [Fact]
57	    public void Lambda_AllZeroWeeks_IsZeroNotNull()
58	    {
59	        // Enough history, no volume: that is a NotEnoughVolume verdict, not InsufficientHistory.
60	        Assert.Equal(0.0, Baseline.Lambda(new int[12])!.Value);
61	    }
62	
63	    [Fact]
64	    public void Lambda_NegativeCount_Throws()
65	    {
66	        Assert.Throws<ArgumentOutOfRangeException>(() => Baseline.Lambda([5, 5, 5, 5, 5, 5, 5, -1]));
67	    }
68	}
69	
```

### Tool result (subagent) · 2026-09-28T12:20:11.422Z

```
1	using RelayPulse.Core.Statistics;
2	
3	namespace RelayPulse.Tests.Statistics;
4	
5	public class BandTests
6	{
7	    [Theory]
8	    [InlineData(3.0, 0, 8)]
9	    [InlineData(6.0, 2, 13)]
10	    [InlineData(25.0, 16, 39)]
11	    [InlineData(75.0, 59, 98)]
12	    [InlineData(10.4, 5, 20)] // account 8's baseline: its 7 sits comfortably inside
13	    public void For_GoldenLambdas_GivesExpectedIntegerBand(double lambda, int lo, int hi)
14	    {
15	        Assert.Equal(new CountBand(lo, hi), Significance.BandFor(lambda));
16	    }
17	
18	    [Fact]
19	    public void For_LambdaFrom01To500_IsNonNegativeAndOrdered()
20	    {
21	        for (var lambda = 0.1; lambda <= 500.0; lambda += 0.1)
22	        {
23	            var band = Significance.BandFor(lambda);
24	
25	            Assert.True(band.Lo >= 0, $"λ={lambda}: lo={band.Lo}");
26	            Assert.True(band.Hi >= band.Lo, $"λ={lambda}: [{band.Lo},{band.Hi}]");
27	        }
28	    }
29	
30	    [Theory]
31	    [InlineData(3.7)]
32	    [InlineData(6.0)]
33	    [InlineData(10.4)]
34	    [InlineData(25.0)]
35	    [InlineData(72.3)]
36	    [InlineData(500.0)]
37	    public void For_Edges_AgreeWithAssess(double lambda)
38	    {
39	        // The band is exactly the set of counts Assess calls Normal — one rule, two views.
40	        var band = Significance.BandFor(lambda);
41	
42	        Assert.Equal(Verdict.Normal, Significance.Assess(band.Lo, lambda).Verdict);
43	        Assert.Equal(Verdict.Normal, Significance.Assess(band.Hi, lambda).Verdict);
44	        Assert.Equal(Verdict.Above, Significance.Assess(band.Hi + 1, lambda).Verdict);
45	        if (band.Lo > 0)
46	        {
47	            Assert.Equal(Verdict.Below, Significance.Assess(band.Lo - 1, lambda).Verdict);
48	        }
49	    }
50	}
51	
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:11.461Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\tests\\RelayPulse.Tests\\Statistics\\AssessTests.cs"
}
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:11.958Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\tests\\RelayPulse.Tests\\Pulse\\PulseBuilderTests.cs"
}
```

### Tool result (subagent) · 2026-09-28T12:20:11.968Z

```
1	using RelayPulse.Core.Statistics;
2	
3	namespace RelayPulse.Tests.Statistics;
4	
5	public class AssessTests
6	{
7	    [Fact]
8	    public void Assess_NullLambda_IsInsufficientHistory()
9	    {
10	        var result = Significance.Assess(12, null);
11	
12	        Assert.Equal(Verdict.InsufficientHistory, result.Verdict);
13	        Assert.Equal(double.PositiveInfinity, result.RankKey);
14	    }
15	
16	    [Theory]
17	    [InlineData(0.0)]
18	    [InlineData(3.0)]
19	    [InlineData(3.69)]
20	    public void Assess_LambdaBelowGate_IsNotEnoughVolumeEvenForExtremeCounts(double lambda)
21	    {
22	        Assert.Equal(Verdict.NotEnoughVolume, Significance.Assess(0, lambda).Verdict);
23	        Assert.Equal(Verdict.NotEnoughVolume, Significance.Assess(40, lambda).Verdict);
24	    }
25	
26	    [Fact]
27	    public void Assess_ZeroEventsAt370_IsBelow()
28	    {
29	        // Just over the gate and over ln 40: an empty week is now detectable.
30	        Assert.Equal(Verdict.Below, Significance.Assess(0, 3.7).Verdict);
31	    }
32	
33	    [Fact]
34	    public void Assess_Account8SevenAgainst104_IsNormalNotAMinus36PercentAlarm()
35	    {
36	        Assert.Equal(Verdict.Normal, Significance.Assess(7, 10.4).Verdict);
37	    }
38	
39	    [Fact]
40	    public void Assess_Account6SpikeAgainstTrimmedBaseline_IsAbove()
41	    {
42	        var result = Significance.Assess(880, 72.3);
43	
44	        Assert.Equal(Verdict.Above, result.Verdict);
45	        Assert.False(double.IsNaN(result.RankKey));
46	        Assert.True(result.RankKey < 1.0);
47	    }
48	
49	    [Fact]
50	    public void Assess_RankKey_EqualsMinOfTailsOverAlphas()
51	    {
52	        const int n = 14;
53	        const double lambda = 6.0;
54	        var expected = Math.Min(
55	            PoissonTail.Lower(n, lambda) / Significance.AlphaLow,
56	            PoissonTail.Upper(n, lambda) / Significance.AlphaHigh);
57	
58	        Assert.Equal(expected, Significance.Assess(n, lambda).RankKey, 12);
59	    }
60	
61	    [Fact]
62	    public void Assess_RankKey_EveryFlaggedRowSortsAboveEveryUnflaggedRow()
63	    {
64	        // Mix of all five verdicts across several λ; ascending RankKey must put all Above/Below
65	        // first, with NotEnoughVolume and InsufficientHistory never interleaving with flags.
66	        var rows = new List<Assessment>();
67	        foreach (var lambda in new double?[] { null, 0.0, 2.5, 3.0, 3.7, 6.0, 10.4, 25.0, 72.3, 500.0 })
68	        {
69	            for (var n = 0; n <= 600; n += n < 50 ? 1 : 25)
70	            {
71	                rows.Add(Significance.Assess(n, lambda));
72	            }
73	        }
74	
75	        var verdicts = rows.Select(r => r.Verdict).ToHashSet();
76	        Assert.Equal(5, verdicts.Count);
77	
78	        var flagged = rows.Where(r => r.Verdict is Verdict.Above or Verdict.Below).ToList();
79	        var unflagged = rows.Where(r => r.Verdict is not (Verdict.Above or Verdict.Below)).ToList();
80	        Assert.True(flagged.Max(r => r.RankKey) < un
```

### Tool result (subagent) · 2026-09-28T12:20:12.462Z

```
1	using RelayPulse.Core.Pulse;
2	using RelayPulse.Core.Statistics;
3	
4	namespace RelayPulse.Tests.Pulse;
5	
6	public class PulseBuilderTests
7	{
8	    // 14 complete Mondays: 2026-02-02 … 2026-05-04.
9	    private static readonly DateOnly[] Weeks =
10	        Enumerable.Range(0, 14).Select(i => new DateOnly(2026, 2, 2).AddDays(7 * i)).ToArray();
11	
12	    private static readonly DateOnly Target = Weeks[^1];
13	
14	    // One (location, week) with n calls, outcome "connected", plus the matching account-grain row.
15	    private static IEnumerable<WeeklyCount> Calls(string location, DateOnly week, int n, string? outcome = "connected") =>
16	        n == 0 ? [] : [new(week, location, "call_received", outcome, n), new(week, null, "call_received", outcome, n)];
17	
18	    private static IEnumerable<WeeklyCount> Steady(string location, int perWeek, int weeks = 14) =>
19	        Weeks.Take(weeks).SelectMany(w => Calls(location, w, perWeek));
20	
21	    [Fact]
22	    public void Build_LocationSilentInTargetWeek_GetsZeroFilledRowAndIsJudged()
23	    {
24	        var rows = Steady("Site A", 20, weeks: 13).ToList();
25	
26	        var report = PulseBuilder.Build(Weeks, Target, rows);
27	
28	        var a = Assert.Single(report.Locations);
29	        Assert.Equal("Site A", a.Location);
30	        Assert.Equal(0, a.Count);
31	        Assert.Equal(Verdict.Below, a.Assessment.Verdict);
32	        Assert.Empty(a.ByType);
33	    }
34	
35	    [Fact]
36	    public void Build_WeeksWithNoEvents_CountAsZeroInTheBaseline()
37	    {
38	        // 10 on alternate weeks, 0 between. Zero-filled trimmed mean of the 12 prior weeks is 5;
39	        // skipping empty weeks instead would give 10.
40	        var rows = Weeks.Where((_, i) => i % 2 == 0).SelectMany(w => Calls("Site A", w, 10)).ToList();
41	
42	        var report = PulseBuilder.Build(Weeks, Target, rows);
43	
44	        Assert.Equal(5.0, report.Locations[0].Assessment.Lambda!.Value, 10);
45	        Assert.Equal(12, report.Locations[0].BaselineWeeks);
46	    }
47	
48	    [Fact]
49	    public void Build_SevenPriorCompleteWeeks_IsInsufficientHistory()
50	    {
51	        var rows = Steady("Site A", 10).ToList();
52	
53	        var report = PulseBuilder.Build(Weeks, Weeks[7], rows);
54	
55	        Assert.Equal(7, report.Account.BaselineWeeks);
56	        Assert.Equal(Verdict.InsufficientHistory, report.Account.Assessment.Verdict);
57	        Assert.Equal(Verdict.InsufficientHistory, report.Locations[0].Assessment.Verdict);
58	    }
59	
60	    [Fact]
61	    public void Build_EightPriorCompleteWeeks_IsJudged()
62	    {
63	        var report = PulseBuilder.Build(Weeks, Weeks[8], Steady("Site A", 10).ToList());
64	
65	        Assert.Equal(Verdict.Normal, report.Account.Assessment.Verdict);
66	    }
67	
68	    [Fact]
69	    public void Build_RowsOutsideTheCompleteWeeksOrAfterTarget_AreIgnored()
70	    {
71	        var rows = Steady("Site A", 10)
72	            .Concat(Calls("Site A", new DateOnly(2026, 1, 26), 500)) // pa
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:15.928Z

**Read**
```json
{
  "file_path": "C:\\demos\\relay-pulse\\CLAUDE.md",
  "offset": 60
}
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:20:16.252Z

**Grep**
```json
{
  "pattern": "2026-09-28",
  "path": "C:\\demos\\relay-pulse\\ai-log\\decisions.md",
  "output_mode": "content"
}
```

### Tool result (subagent) · 2026-09-28T12:20:16.451Z

```
60	in migrations and views, never by rewriting the seed.
61	
62	**No `DateTime.UtcNow` / `DateTime.Now` / `Date.now()` in domain, query, or view code.** Two
63	separate reasons, both load-bearing:
64	- *Correctness*: the dataset ends `2026-07-27`, roughly two months before wall clock. Anything
65	  keyed off the system clock computes an empty current week. The current period comes from
66	  `MAX(occurred_at)`, not from now.
67	- *Testability*: golden-value tests must be stable forever.
68	
69	Inject an `IClock` where a real timestamp is genuinely needed (review-state `created_at`), and
70	derive every reporting period from the data. A grep for `UtcNow` outside composition root and
71	clock implementation should return nothing.
72	
73	**Tests assert golden values produced by `tools/profile_seed.py --dedupe`** — an independent
74	implementation, in another language, written before any app code. Do not "fix" a failing test by
75	copying what the C# returned; re-derive from the profiler and find out which one is wrong.
76	
77	**Unit tests run with zero infrastructure.** Bucketing, bands, verdicts and window completeness
78	must not need Docker. Integration tests use Testcontainers and skip with a clear message when
79	Docker is absent, so `dotnet test` is green either way.
80	
81	**Log decisions in `ai-log/decisions.md`.** Format is at the top of that file: times in
82	America/Bogota, `time · ACCEPTED|REJECTED|REDIRECTED · what · why · ref`.
83	
84	## Domain rules
85	
86	These come from profiling the seed. Violating one produces plausible, wrong numbers.
87	
88	**Time**
89	- `occurred_at` is UTC. Bucket in the **account's own IANA timezone**, Mon–Sun weeks.
90	- Never bucket in UTC. 8 events land in a different week under UTC — small, and exactly the
91	  regression that will otherwise ship silently.
92	- Six zones in play, including `America/Phoenix` (no DST) and a literal `UTC`.
93	- A week is **complete** only when all 7 days fall inside the observation window. Completeness is
94	  a property of the window, not of where events land — a week with zero events can be complete.
95	- **Partial weeks are never compared, never used in a baseline, and never the default view.**
96	  Both edge buckets (`2026-01-26`, `2026-07-27`) cover 1/7 days for all 20 accounts.
97	- Default view is the **last complete week**: `2026-07-20 … 2026-07-26`.
98	
99	**Identity**
100	- Location key is `(account_id, location)`. Labels are generic (`Site A`…`Site O`) and reused
101	  across accounts — account 1's Site A is not account 6's Site A.
102	- `accounts.created_at` is **not** the start of history. Every account's events start `2026-02-01`
103	  regardless. Never window on `created_at`.
104	- One timezone per account; the schema cannot express per-location zones (known limitation).
105	
106	**Reads**
107	- Raw-SQL result rows must not carry `DateTime` unless mapped: the UTC converter reaches
108	  `SqlQueryRaw<DateTime>` scalars (via `DefaultTypeMapping`) but **not** `Dat
```

### Tool result (subagent) · 2026-09-28T12:20:16.823Z

```
18:2026-09-28 11:17 · ACCEPTED · Adopt from VerusLLC (read-only reference): .slnx with src/ + tests/ folders; Directory.Packages.props with EF/AspNetCore 10.0.x and Testcontainers.MsSql in place of .PostgreSql · cheap, consistent with prior work, one place for versions · VerusLLC review (T0)
19:2026-09-28 11:17 · ACCEPTED · Add Directory.Build.props (VerusLLC had none): net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors · set once instead of per csproj across 3 projects · VerusLLC review (T0)
20:2026-09-28 11:17 · ACCEPTED · Per-project AddCore() IServiceCollection extension; IClock registered only there · single composition point and the only legitimate home for a real clock · VerusLLC review (T0), CLAUDE.md
21:2026-09-28 11:17 · ACCEPTED · CancellationToken on every async path · free; SqlQueryRaw accepts it · VerusLLC review (T0)
22:2026-09-28 11:17 · ACCEPTED · ProblemDetails for 4xx with a machine-readable code as title; 400 for bad params only, never 404 for account 20 · stable error shape the Angular client can switch on · VerusLLC review (T0), CLAUDE.md edge cases
23:2026-09-28 11:17 · ACCEPTED · xUnit v3 + plain Assert; WebApplicationFactory tests asserting on JsonElement; Method_Condition_Outcome names; public partial class Program · asserts the JSON contract the goldens are about; FluentAssertions 8 needs a commercial licence and adds nothing · VerusLLC review (T0)
24:2026-09-28 11:17 · ACCEPTED · Structured log templates with a "Pulse:" prefix; compose with SQL Server healthcheck + depends_on service_healthy · free queryability; SQL Server is slow to start and the API must not race it · VerusLLC review (T0)
25:2026-09-28 11:17 · REJECTED · VerusLLC's separate Domain/Application/Persistence projects · plan fixes Api + Core + Tests; two extra layers for one endpoint cost time and buy nothing · VerusLLC review (T0), PLAN.md
26:2026-09-28 11:17 · REJECTED · MediatR + pipeline behaviours, FluentValidation, Polly retry, Result<T>/Error framework · one endpoint with ~2 failure modes; call the service directly and guard 3 query params inline; retries hide failures in a demo · VerusLLC review (T0)
27:2026-09-28 11:17 · REJECTED · MVC controllers + ApiControllerBase, API versioning (4 packages), ApiExceptionFilterAttribute · minimal API MapGet on a route group is less code; use built-in AddProblemDetails() + UseExceptionHandler() · VerusLLC review (T0)
28:2026-09-28 11:17 · REJECTED · Serilog and correlation-ID middleware · built-in console logger suffices; no distributed calls to correlate · VerusLLC review (T0)
29:2026-09-28 11:17 · REJECTED · Global rate limiter (2 req/min) and CORS AllowAnyOrigin + AllowCredentials · limiter breaks a dashboard that calls the API repeatedly; that CORS combination is rejected by ASP.NET Core, use WithOrigins(localhost:4200) or the Angular dev proxy · VerusLLC review (T0)
30:2026-09-28 11:17 · REJECTED · Scalar UI (unless it's a one-line add) · built-in OpenAPI JSON is enough · VerusLLC review (T0)
31:202
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:21:35.243Z

**Grep**
```json
{
  "pattern": "trim|0\\.005|0\\.025|band|lgamma|72\\.|10\\.8|10\\.4|baseline",
  "path": "C:\\demos\\relay-pulse\\tools\\profile_seed.py",
  "output_mode": "content",
  "-i": true
}
```

### Tool result (subagent) · 2026-09-28T12:21:35.775Z

```
94:    Per DASH-247: partial weeks are never compared and never feed a baseline.
434:    rep.line("baseline. The default dashboard view is the LAST COMPLETE week.")
456:    rep.sub("Partial buckets (excluded from every comparison and baseline)")
524:    rep.heading("7b. Baseline noise per (account, location) - complete weeks only")
526:    rep.line("excluded so a single spike does not inflate its own baseline.")
612:    rep.sub("Baseline noise per account (complete weeks, anomaly weeks excluded)")
665:    rep.line("  CONSEQUENCE: do not try to resolve it - make the baseline robust either")
666:    rep.line("  way. A median/MAD baseline is unmoved by one week at 12x whether that")
667:    rep.line("  week was real demand or a bad import; a mean-based baseline is wrong in")
668:    rep.line("  both cases. The week is excluded from the baselines above and still")
675:    These are held out of every baseline so an anomaly cannot inflate the
676:    baseline it is being measured against.
837:    cur = conn.execute(f"SELECT COUNT(*) c FROM {source} WHERE TRIM(location) = ''")
841:    cur = conn.execute(f"SELECT COUNT(*) c FROM {source} WHERE TRIM(COALESCE(outcome,'x')) = ''")
884:    rep.line("   so created_at is NOT the start of history and must not gate the baseline.")
956:    # Anomalous weeks are found first so the baseline sections can exclude them.
957:    # Excluding an anomaly from its own baseline is what makes the baseline
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:21:39.863Z

**Grep**
```json
{
  "pattern": "72\\.3|72\\.1|10\\.8|10\\.4|\\[0,\\s*8\\]|59|0\\.005|trim",
  "path": "C:\\demos\\relay-pulse\\PLAN.md",
  "output_mode": "content"
}
```

### Tool result (subagent) · 2026-09-28T12:21:40.445Z

```
66:λ=10.4 is 5.1–15.7, so 7 is an ordinary week. Meanwhile account 19's 13 vs 8 (+62%) sits right at
222:| Verdict calibration | acct 8 (7 vs λ=10.4) → **normal**, not a −36% alarm; acct 6 spike week → **above normal** |
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:21:46.137Z

**Grep**
```json
{
  "pattern": "location|occurred_at|outcome",
  "path": "C:\\demos\\relay-pulse\\db\\schema.sql",
  "output_mode": "content"
}
```

### Tool result (subagent) · 2026-09-28T12:21:46.701Z

```
4:-- All occurred_at values are stored in UTC.
17:    location          VARCHAR(80)  NOT NULL,  -- the account's site/branch where the activity occurred
19:    occurred_at       TIMESTAMP    NOT NULL,  -- UTC
21:    outcome           VARCHAR(40)  NULL       -- e.g. 'connected' | 'missed' | 'voicemail' | 'converted' | 'no_show'; may be NULL
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:21:46.977Z

**Grep**
```json
{
  "pattern": "DedupView|utc_start|utc_end|week_start_local|WeekStartLocal|UtcStart",
  "path": "C:\\demos\\relay-pulse\\src",
  "output_mode": "content"
}
```

### Tool result (subagent) · 2026-09-28T12:21:47.513Z

```
src\RelayPulse.Core\Time\WeekBucket.cs:5:/// [<see cref="UtcStart"/>, <see cref="UtcEnd"/>) so events can be bucketed with a range join.
src\RelayPulse.Core\Time\WeekBucket.cs:13:    DateOnly WeekStartLocal,
src\RelayPulse.Core\Time\WeekBucket.cs:14:    DateTime UtcStart,
src\RelayPulse.Api\Pulse\PulseQuery.cs:19:        SELECT b.week_start_local AS WeekStart,
src\RelayPulse.Api\Pulse\PulseQuery.cs:28:         AND b.week_start_local >= @from
src\RelayPulse.Api\Pulse\PulseQuery.cs:29:         AND b.week_start_local <= @to
src\RelayPulse.Api\Pulse\PulseQuery.cs:30:        JOIN {RelayDbContext.DedupView} e
src\RelayPulse.Api\Pulse\PulseQuery.cs:32:         AND e.occurred_at >= b.utc_start
src\RelayPulse.Api\Pulse\PulseQuery.cs:33:         AND e.occurred_at <  b.utc_end
src\RelayPulse.Api\Pulse\PulseQuery.cs:36:            (b.week_start_local, e.location, e.event_type, e.outcome),
src\RelayPulse.Api\Pulse\PulseQuery.cs:37:            (b.week_start_local, e.event_type, e.outcome)
src\RelayPulse.Api\Pulse\PulseEndpoints.cs:51:            .OrderBy(b => b.WeekStartLocal)
src\RelayPulse.Api\Pulse\PulseEndpoints.cs:52:            .Select(b => b.WeekStartLocal)
src\RelayPulse.Api\Data\RelayDbContext.cs:9:    public const string DedupView = "activity_events_dedup";
src\RelayPulse.Api\Data\RelayDbContext.cs:60:            e.HasKey(b => new { b.Timezone, b.WeekStartLocal });
src\RelayPulse.Api\Data\RelayDbContext.cs:62:            e.Property(b => b.WeekStartLocal).HasColumnName("week_start_local");
src\RelayPulse.Api\Data\RelayDbContext.cs:63:            e.Property(b => b.UtcStart).HasColumnName("utc_start");
src\RelayPulse.Api\Data\RelayDbContext.cs:64:            e.Property(b => b.UtcEnd).HasColumnName("utc_end");
src\RelayPulse.Api\Data\RelayDbContext.cs:67:            // The aggregation range-joins on (timezone, utc_start <= occurred_at < utc_end).
src\RelayPulse.Api\Data\RelayDbContext.cs:68:            e.HasIndex(b => new { b.Timezone, b.UtcStart })
src\RelayPulse.Api\Data\RelayDbContext.cs:70:                .IncludeProperties(b => new { b.UtcEnd, b.WeekStartLocal, b.IsComplete })
src\RelayPulse.Api\Data\RelayDbContext.cs:71:                .HasDatabaseName("ix_week_buckets_timezone_utc_start");
src\RelayPulse.Api\Data\Seeding\SeedImporter.cs:137:        const string From = " AS Value FROM " + RelayDbContext.DedupView;
src\RelayPulse.Api\Data\Migrations\RelayDbContextModelSnapshot.cs:115:                    b.Property<DateOnly>("WeekStartLocal")
src\RelayPulse.Api\Data\Migrations\RelayDbContextModelSnapshot.cs:117:                        .HasColumnName("week_start_local");
src\RelayPulse.Api\Data\Migrations\RelayDbContextModelSnapshot.cs:125:                        .HasColumnName("utc_end");
src\RelayPulse.Api\Data\Migrations\RelayDbContextModelSnapshot.cs:127:                    b.Property<DateTime>("UtcStart")
src\RelayPulse.Api\Data\Migrations\RelayDbContextModelSnapshot.cs:129:                        .HasColumnName("utc_start");
src\RelayPulse.Api\D
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:22:09.799Z

**Grep**
```json
{
  "pattern": "class \\w+|WebApplicationFactory|MsSql",
  "path": "C:\\demos\\relay-pulse\\tests",
  "glob": "*.cs",
  "output_mode": "content"
}
```

### Tool result (subagent) · 2026-09-28T12:22:10.321Z

```
tests\RelayPulse.Tests\Time\WeekBucketGeneratorTests.cs:5:public class WeekBucketGeneratorTests
tests\RelayPulse.Tests\Time\TimeZoneResolutionTests.cs:5:public class TimeZoneResolutionTests
tests\RelayPulse.Tests\Statistics\PoissonTailTests.cs:5:public class PoissonTailTests
tests\RelayPulse.Tests\Statistics\BaselineTests.cs:5:public class BaselineTests
tests\RelayPulse.Tests\Statistics\BandTests.cs:5:public class BandTests
tests\RelayPulse.Tests\Statistics\AssessTests.cs:5:public class AssessTests
tests\RelayPulse.Tests\Pulse\PulseBuilderTests.cs:6:public class PulseBuilderTests
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:22:13.572Z

**Grep**
```json
{
  "pattern": "def |per week|weekly",
  "path": "C:\\demos\\relay-pulse\\tools\\profile_seed.py",
  "output_mode": "content",
  "-i": true
}
```

### Tool result (subagent) · 2026-09-28T12:22:14.102Z

```
5:shape, quality and time-bucketing characteristics of the dataset. Weekly
53:def load_db(db_dir: Path) -> sqlite3.Connection:
67:def parse_ts(raw: str) -> datetime:
72:def week_start(d: date) -> date:
77:def iter_weeks(first: date, last: date):
97:    def __init__(self, start: date, end: date):
106:    def last_complete(self) -> date | None:
110:    def is_partial(self, w: date) -> bool:
113:    def coverage(self, w: date) -> tuple[date, date]:
122:def modified_z(values: list[float]) -> list[float]:
137:def fmt_table(headers: list[str], rows: list[list], markdown: bool = False) -> str:
160:    def __init__(self, markdown: bool):
164:    def heading(self, text: str) -> None:
170:    def sub(self, text: str) -> None:
173:    def line(self, text: str = "") -> None:
176:    def table(self, headers: list[str], rows: list[list]) -> None:
182:    def render(self) -> str:
190:def section_overview(conn, rep: Report, events, accounts):
210:def section_per_account(conn, rep: Report, events, accounts):
251:def section_types_outcomes(conn, rep: Report, events):
308:def section_duplicates(conn, rep: Report, events, windows):
396:def build_weeks(events, accounts):
397:    """Per-account weekly aggregates in the account's own timezone."""
428:def section_weekly(rep: Report, accounts, weeks, windows):
429:    rep.heading("6. Weekly counts per account (account-local tz, Mon-Sun)")
487:def section_locations(rep: Report, accounts, weeks, windows, spike_weeks):
488:    rep.heading("7. Weekly counts per location (account-local tz, complete weeks)")
525:    rep.line("CV = stdev/mean of the weekly series. Anomaly weeks (section 8) are")
569:def section_outliers(rep: Report, accounts, weeks, windows, events, spike_weeks):
672:def find_anomalous_weeks(accounts, weeks, windows) -> dict[int, set]:
692:def _daterange(a: date, b: date):
699:def section_oddities(conn, rep: Report, events, accounts, weeks, cutoff_local, source):
756:    rep.line("-> strong weekday effect; Mon-Sun weekly buckets absorb it, but any")
904:def main() -> int:
965:    section_weekly(rep, accounts, weeks, windows)
```

### 🤖 Agent → tool (subagent) · 2026-09-28T12:23:20.599Z

**SubagentHandback**
```json
{
  "message": "I found no blocker-level correctness defects in the statistics, SQL or zero-fill code. The most serious problem is that nothing automatically tests the SQL and the endpoint.\n\n## Confirmed defects (most severe first)\n\n**1. `tests/RelayPulse.Tests/` (no file) \u00b7 major \u00b7 the SQL and the endpoint have no automated tests at all.**\n- **Evidence:** the only test classes are PoissonTail, Baseline, Band, Assess, PulseBuilder, WeekBucketGenerator and TimeZoneResolution. There is no WebApplicationFactory test and no Testcontainers test. CLAUDE.md requires API tests through WebApplicationFactory that assert on the wire JSON.\n- **Failing scenario:** any of these changes would still leave all 80 tests passing:\n  - replacing `{RelayDbContext.DedupView}` with `activity_events` in PulseQuery.cs:30;\n  - dropping `b.is_complete = 1` (PulseQuery.cs:27);\n  - joining on a UTC date instead of `utc_start`/`utc_end`;\n  - removing the `JsonStringEnumConverter` (verdicts would go out as integers);\n  - returning 404 for account 20.\n- Your live Python cross-check verified this once; nothing guards it from here on.\n- **Fix:** add integration tests (skip when Docker is absent) that assert:\n  - acct 1 week 2026-07-20: 53, and A=9 B=10 C=9 D=9 E=9 F=7;\n  - acct 6 Site E week 2026-02-23: count 2 (the dedupe golden);\n  - acct 20: 200, `\"state\":\"empty\"`, `insufficient_history`;\n  - `?week=2026-07-27` and `?week=2026-07-21`: 400 with `title` `pulse.invalid_week`;\n  - an unknown id: 404 `pulse.account_not_found`;\n  - verdicts in snake_case strings.\n\n**2. CLAUDE.md:153-155,158 plus AssessTests.cs:34-37 and BandTests.cs:12 \u00b7 major (rule 11) \u00b7 some goldens don't come from the oracle, and one test uses a stale \u03bb.**\n- **Evidence:** the table says it comes \"From `python tools/profile_seed.py --dedupe`\". A grep of profile_seed.py finds no trimmed mean, no Poisson tail, no alpha values and no bands, and none of 72.1, 72.3 or 10.8. It does print the weekly counts, so the 72.3 fixture in BaselineTests can be re-derived from them by hand. But \u03bb=72.1, the band table and \u03bb=10.8 came from ad-hoc scratchpad scripts that aren't checked in.\n- **Stale test value:** `Assess_Account8SevenAgainst104` and the `[InlineData(10.4, 5, 20)] // account 8's baseline` case use 10.4. That is the old exclude-anomaly baseline from PLAN.md:66/222; the current golden is 10.8. Both tests hard-code \u03bb, so they would pass whatever Baseline computes from acct 8's real series.\n- **Fix:** check in the oracle for tails, bands and the trimmed mean (extend profile_seed.py or add a sibling script), and cite it in the table and the test comments. Relabel 10.4 as a synthetic \u03bb, or change it to 10.8 and derive it from acct 8's weekly counts.\n\n**3. PulseBuilder.cs:34-39 and 42-45 against the PulseReport.cs:12 doc \u00b7 minor \u00b7 \"empty\" and the list of locations are decided from the 13-week fetched slice only.**\n- **Evidence:** `relevant` only holds rows from `[completeWeeks[index-12], target]`, because that is all PulseEndpoints.cs:70-71 fetches. The doc says HasEvents is false only when there are \"no events in any complete week up to the target\".\n- **Failing scenario:** an account whose last event is more than 13 weeks before the target gets `state:\"empty\"`, `insufficient_history` and `BaselineWeeks=12`. That contradicts itself: it has 12 weeks of history, all zero, so the result should be \u03bb=0 and `not_enough_volume`. In the same way, a location silent for the whole slice disappears instead of getting a zero-filled row.\n- This can't happen with the seed, since every account and location runs from 2026-02-01.\n- **Fix:** fetch the account's distinct locations, or an existence flag, over all complete weeks up to the target. Otherwise, narrow the doc to \"in the baseline window\".\n\n**4. PulseEndpoints.cs:55-58 \u00b7 minor \u00b7 `completeWeeks.Count == 0` returns 400 `pulse.invalid_week` even when no `w
```

### Tool result (subagent) · 2026-09-28T12:23:23.350Z

```
{"success":true,"message":"Report delivered to your caller."}
```

### 🤖 Agent (subagent) · 2026-09-28T12:23:33.612Z

The full review went back to the agent that launched it; the file paths are listed there.

There are no blocker-level bugs in the statistics, the SQL or the zero-fill. The most serious problem is that nothing automatically tests the SQL query or the endpoint. Findings, most severe first:

1. **Major: the SQL and the `/pulse` endpoint have no automated tests.** There is no WebApplicationFactory test and no Testcontainers test. If someone pointed the query at the raw table, dropped the complete-week filter, bucketed by UTC date or removed the enum converter, all 80 tests would still pass. The live check you did against Python only protects this once. Fix: add Docker-skippable integration tests that assert the goldens (acct 1's 53 and per-site counts, acct 6 Site E = 2, acct 20 returns 200 with an empty state, the 400 and 404 codes).
2. **Major: some goldens and tests don't come from the oracle.**
   - CLAUDE.md says the golden table comes from `profile_seed.py`, but the profiler computes no trimmed mean, tails or bands. λ=72.1, λ=10.8 and the band table came from scratchpad scripts that aren't checked in.
   - Two tests still use λ=10.4, the old baseline, and call it "account 8's baseline"; the current golden is 10.8. Both hard-code λ, so they would pass whatever `Baseline` computes for account 8.
3. **Minor: "empty" and the list of locations are decided from only the 13 weeks fetched.** An account with no events in the 13 weeks up to the target is reported "empty" with `insufficient_history`. It should be λ=0 and `not_enough_volume`. A location silent for those 13 weeks also disappears instead of showing a zero row. The seed can't trigger this.
4. **Minor: with no complete weeks at all, a request with no `week` parameter gets a 400.** That breaks the "400 for bad parameters only" rule. The seed can't trigger this either.
5. **Minor: the new `decisions.md` entries are stamped 07:16 but sit after 11:35 entries from the same day.** Either they or the earlier entries aren't in Bogota time.

Checked and found correct:
- **`PoissonTail`:** always terminates, never returns NaN and stays accurate at λ=500.
- **`BandFor`:** the search limits are safe across the whole λ range.
- **`BandFor` and `Assess`:** they cannot disagree, because both use the same tail functions and a count can't be flagged as both below and above.
- **Ranking:** every flagged row sorts above every unflagged one.
- **The SQL:** parameterised, reads the dedupe view, buckets in the account's timezone and can't double-count across the two grouping sets, because `location` is NOT NULL.
- **Window edges:** they behave correctly.

Judgement calls, not defects:
- Rows under the volume gate still show a band, so a count outside it appears with no flag.
- When a p-value underflows to 0, the most extreme flags are ordered by name rather than by how extreme they are.
- The zero-fill is only right because the endpoint fetches exactly the 12 prior weeks; changing that fetch would silently corrupt baselines.
- The sum-to-one tail test can't catch errors in very small tails.
