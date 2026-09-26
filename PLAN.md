# DASH-247 — Implementation Plan

> On approval this lands in the repo as `PLAN.md`.

## Context

Relay customers see raw totals per location and can't tell whether this week's numbers are
good, bad, or typical. Account managers field "is this normal for us?" by hand; support hears
that multi-location customers can't spot which site needs attention.

Profiling the seed (`tools/profile_seed.py`, 12,626 events / 20 accounts / 26 weeks) produced
one finding that determines the whole design:

**Weekly counts are statistically indistinguishable from Poisson arrivals.** Fano factor
(variance/mean) is 0.93 median at account grain and 0.93 at location grain, range 0.34–2.28.
Several accounts are *under*dispersed.

That means the week-to-week "variation" customers are staring at is almost entirely arrival
randomness. There is no trend to read, and the spread scales as √λ — so the same percentage
move means completely different things at 5 events/week and 50. The feature is therefore not
a chart and not a percentage threshold. It is a **volume-aware significance test** that tells
a customer when a number is outside what randomness explains, and stays quiet otherwise.

---

## 1. Interpretation of "is this normal for us?"

| | Decision |
|---|---|
| **Baseline** | The series' own prior **complete** weeks, Poisson-modelled. Expected rate λ = mean of the baseline window; normal band = λ ± 2.576·√λ. Detected anomaly weeks are excluded from the baseline. |
| **Window** | Trailing **12 complete weeks**, minimum 8, ending the week before the one being judged. |
| **View** | Landing page = **ranked location list** for one account, ordered by how far outside the band each location's week sits, with the account total as a header verdict. |
| **Period** | The **last complete** Mon–Sun week in the account's own timezone (`2026-07-20 … 2026-07-26` for this seed), with navigation to earlier complete weeks. Partial weeks are never shown as a verdict. |
| **Verdict vocabulary** | `above normal` / `below normal` / `normal` / `not enough volume` — four states, no numeric score in the primary UI. |

**Why 2.576 (99%), not a conventional 95%.** Calibrated against the seed, not by convention.
Across all 25×69 location-weeks:

| band | flag rate | a 15-location account sees | spike week caught | default week |
|---|---|---|---|---|
| 90% | 13.0% | 1.96 flags/wk | 22 locs | 4 locs |
| 95% | 6.6% | 0.98 flags/wk | 21 locs | 1 loc |
| **99%** | **1.8%** | **0.27 flags/wk (~1/month)** | **16 locs** | **1 loc** |
| 99.7% | 1.4% | 0.21 flags/wk | 16 locs | 0 locs |

At 95% a 15-location customer gets a false flag *every week* and stops trusting the page. 99%
gives roughly one per month and still catches the large anomaly. (Empirical 1.8% vs theoretical
1.0% reflects mild overdispersion in some series — the estimator below absorbs that.)

**Quasi-Poisson, not pure Poisson.** Estimate a dispersion factor φ = Fano of the baseline
window, clamp to `[1, 4]`, and widen the band to λ ± 2.576·√(φλ). On this seed φ≈1 and it is a
no-op; on real data with seasonality or campaigns it prevents the band being too tight. Five
lines, and it is the difference between a model that only works on synthetic data and one that
degrades honestly.

**Minimum volume gate.** Below λ = 3/week nothing short of a doubling is detectable, so those
rows report `not enough volume to judge` rather than a verdict. This affects a real share of the
data: 49 of 69 locations average under 8 events/week.

### Alternatives considered and rejected

**A. Percent change vs a trailing average ("last week vs 4-week average, flag ±20%").**
Rejected: provably wrong on this data. With spread ∝ √λ, a fixed percentage is far too tight for
small locations and too loose for large ones. Worked example from the seed — account 8 ran 7
events against an 11 median, **−36%**, which a percentage rule flags loudly; the Poisson band for
λ=10.4 is 5.1–15.7, so 7 is an ordinary week. Meanwhile account 19's 13 vs 8 (+62%) sits right at
its boundary and is genuinely marginal. A percentage rule gets both backwards.

**B. Median/MAD modified z-score (what `profile_seed.py` uses for discovery).**
Rejected for the product, correctly used in the profiler. MAD on 12 small integers is extremely
coarse — for a location averaging 5/week, MAD is typically 1 or 2, so the z-score jumps in large
discrete steps and the band is unstable. It is the right robust tool for *finding* outliers in a
fixed dataset offline; it is the wrong tool for a per-request band on small counts. Robustness is
still needed, and is supplied instead by excluding detected anomaly weeks from the baseline.

---

## 2. Open questions for product

Each has a working assumption; none blocks implementation.

| # | Question | Working assumption |
|---|---|---|
| 1 | Normal relative to the account's own history, or to industry peers? | **Own history.** Peer benchmarking is a different product with consent and cohort-sizing problems. |
| 2 | Is "activity" all events, or should calls / leads / appointments be judged separately? | **Flag on total events**; show the per-type split in each row for context. A location can have flat totals while appointments collapse — noted as a real gap. |
| 3 | Should a drop be as prominent as a spike? | **Yes, both.** Drops are likelier to be actionable (phones down, staff out). |
| 4 | Is "mark reviewed" enough, or do admins need assignment/escalation? | **Reviewed + optional free-text note**, no assignment. |
| 5 | Should a week confirmed as real business (a campaign) still be excluded from future baselines? | **Excluded by default**, and the review note is where a customer says otherwise. Revisit — a customer may reasonably want a genuine step-change absorbed into normal. |
| 6 | Is Mon–Sun the right period, or do some customers want month? | **Mon–Sun**, matching "Monday morning". |
| 7 | **Can one account's locations span timezones?** | **No** — schema only offers `accounts.timezone`. Flagged as a schema limitation below; a 15-location group plausibly spans zones and the current model cannot express it. |
| 8 | Is the 2-month gap between last event and wall clock normal pipeline lag or an export artifact? | **Artifact.** Anchor on `MAX(occurred_at)` and always label the period on screen. |

---

## 3. How each data finding is handled

| Finding | Handling |
|---|---|
| **12 exact duplicates**, 7 accounts, 11 location-weeks | Dedupe in a **SQL view** over the raw table (`GROUP BY` all columns but `id`, keep `MIN(id)`). Seed and raw table untouched. All reads go through the view. Justified: the overcount is negligible account-wide but **1–50% (median 12%) per location-week**, and every one lands in a complete week. |
| **Acct 6, 2026-06-03, 805 events (80× its daily median)** | **Cause is an open question, not a finding** — the shape fits a bulk import and a real demand event equally, and nothing in the data separates them. Handled so the answer doesn't matter: the week is detected, **excluded from the baseline** (acct 6 baseline mean 72.7 rather than 105.0), and still surfaced as an anomaly. Correct under either cause. |
| **Account 20 — zero events** | First-class empty state. Returns HTTP 200 with a populated window and `insufficient_history`, never 404 and never a divide-by-zero. It is an integration test case. |
| **Timezones** (6 zones incl. `America/Phoenix` no-DST and a literal `UTC`) | Bucketing via a precomputed `week_buckets` dimension (below). 8 events change week under UTC bucketing — small, and the direct test that the logic is right. |
| **Partial weeks** | Materialised as `week_buckets.is_complete`. Both edge buckets are 1/7 days for all 20 accounts. Never compared, never in a baseline, never the default view. Excluded by a column, not by a special case at each call site. |
| **NULL outcome (398, 3.2%)** | A normal state, not corruption. Counted in totals; shown as `unspecified` in the per-type split. Never silently dropped. |
| **Outcome vocabulary** | 7 values, not the 5 in the docs — `completed` and `open` are undocumented, and outcome is a **per-type enum** (calls→connected/missed/voicemail, appointments→completed/no_show, leads→converted/open). Modelled as a lookup, not a shared enum. |
| **`duration_seconds` is noise** | Clamped [20,1500], uncorrelated with outcome (missed calls median 711s). **Not used for any metric.** |
| **Low volumes** — 49/69 locations under 8/week | The `not enough volume` verdict plus the λ≥3 gate. This is the single biggest source of would-be false positives. |
| **`created_at` is not the start of history** | Ignored for windowing; the observation window comes from the data. |
| **Hour-of-day is a generator artifact** | No hour-of-day feature. Day/week bucketing is unaffected. |
| **Cutoff is 2 months stale and is a Monday** | Anchor on `MAX(occurred_at)`; the period is always rendered as an explicit date range. |

---

## 4. Architecture and stack

**SQL Server** (chosen) · .NET 8 Web API · EF Core 8 · Angular 17 standalone + signals.

### Timezone strategy — a `week_buckets` dimension, not `AT TIME ZONE`

SQL Server's `AT TIME ZONE` expects Windows timezone names on Windows hosts, but the seed carries
IANA names. Rather than ship an IANA→Windows mapping, precompute buckets in C#:

```
week_buckets(timezone, week_start_local, utc_start, utc_end, is_complete)
```

Generated once by a seeder using `TimeZoneInfo` (.NET 6+ resolves IANA IDs on Windows via ICU —
**verified in task T0 before anything is built on it**), for the 6 timezones present across the
observation window. Aggregation becomes a range join on an indexed `occurred_at`:

```
JOIN week_buckets b ON b.timezone = a.timezone
                   AND e.occurred_at >= b.utc_start
                   AND e.occurred_at <  b.utc_end
```

Why this over the mapping table: it is portable across engines, DST-correctness is computed by
.NET's tz database rather than the DB's, **it materialises the partial-week decision as a column**,
and the bucket table is independently testable. Cost is one small dimension table.

### ORM vs raw SQL — both, split on purpose

- **EF Core** for schema, migrations (`dotnet ef migrations add`), the seed import, and all
  review-state writes. Change tracking earns its place on the write side.
- **Raw parameterised SQL** via `Database.SqlQueryRaw<T>` for the single aggregation query.
  That query is the product; it must be legible and reviewable. LINQ over a range join plus
  `GROUPING SETS` would either not translate or generate something nobody can review. No Dapper —
  not worth a second data-access library for one query.

**One statement, both grains.** Account row and location rows come back from one query using
`GROUPING SETS`, not two round trips.

### Endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/accounts` | Account picker (no auth — see Deferred). |
| `GET` | `/api/accounts/{id}/pulse?week=YYYY-MM-DD` | **The aggregation.** Defaults to last complete week. Returns period, account verdict, ranked location rows (count, expected λ, band, verdict, per-type split), and each row's review state. |
| `PUT` | `/api/accounts/{id}/reviews/{location}/{week}` | Upsert review state (reviewed flag + note). |

Review state is **server-persisted** — `localStorage` would be a weaker reading of "state survives
reload" and wouldn't survive a different browser.

### Angular state

Signals plus one service. **No NgRx**: one screen, one resource: the ceremony would cost an hour
and demonstrate nothing a reviewer can't already see. Week navigation and selected account live in
the URL so a customer can link an account manager straight to the week in question.

---

## 5. Tasks and time boxes (~5h)

| # | Task | Box | Notes |
|---|---|---|---|
| T0 | Solution skeleton; SQL Server up; **verify `TimeZoneInfo` resolves IANA IDs on Windows** | 25m | Hard gate. If it fails, fall back to a 6-row IANA→Windows map — decided here, not mid-build. |
| T1 | EF Core migrations (accounts, activity_events, dedupe view, week_buckets, review state); seed import; bucket generator | 50m | Real migrations, not `EnsureCreated`. |
| T2 | Aggregation query + quasi-Poisson band + verdict logic + `/pulse` endpoint | 75m | The core. Highest value per minute. |
| T3 | Tests: unit (bucketing, bands, verdicts) + integration goldens | 55m | Written against §6 values. |
| T4 | Angular: ranked location list, verdicts, week navigation, account picker | 70m | |
| T5 | Review state: endpoint + UI toggle + note | 35m | The persisted input. |
| T6 | README (decisions, trade-offs, how to run, what's missing) | 25m | Non-negotiable; this is where judgment is visible. |
| — | Buffer | 30m | |
| | **Total** | **~5h05** | |

### Stop rule

1. **Per task:** overrun the box by >50% → take the documented fallback and move on. Do not debug
   into the next box.
2. **At 3h30**, T0–T2 must be green against the §6 goldens. If not, **cut T5 entirely** and ship a
   correct read-only dashboard. A correct small slice beats a broken interactive one — and the
   aggregation is the thing being assessed.
3. **Hard stop at 5h30.** Whatever is unfinished is written up in the README as a known gap with
   the approach I would have taken. An honest gap costs less than a broken feature.
4. **Never cut:** the dedupe view, the complete/partial distinction, the empty-account path, and
   T6. These are correctness and judgment, not features.

---

## 6. Test strategy

**What matters, in order:** (1) bucketing lands events in the right account-local week;
(2) partial weeks are excluded everywhere; (3) the baseline is unmoved by the anomaly;
(4) dedupe is applied on every read path; (5) empty/low-volume accounts degrade gracefully.
Verdict *wording* and UI layout are not worth test budget.

**Where the golden values come from.** `tools/profile_seed.py` — an independent implementation, in
a different language, computed directly from the same `seed.sql`. It was written before any
application code and cross-checked against the raw SQL. That makes it a genuine oracle rather than
a restatement of the implementation. `--dedupe` reproduces exactly what the API should serve.

| Assertion | Golden |
|---|---|
| Row counts, raw vs deduped | 12,626 → **12,614**; 12 groups, 7 accounts, 11 location-weeks |
| Window shape (every account) | 27 buckets, **25 complete**, last complete **2026-07-20**; both partial buckets 1/7 days |
| Account totals, last complete week | acct 1 = **53**, 6 = **87**, 8 = **7**, 18 = **20**, 19 = **13**, 20 = **0** |
| Acct 1 per-location, same week | A=9, B=10, C=9, D=9, E=9, **F=7** |
| Anomaly excluded from baseline | acct 6 week `2026-06-01` = **880**; baseline mean **72.7** excluding it, 105.0 including — asserts the exclusion, not just the detection |
| Dedupe at reporting grain | acct 6 / Site E / week `2026-02-23`: raw **3** → deduped **2** |
| **Timezone correctness** | Exactly **8** events change week under UTC bucketing (accts 1:2, 3:1, 6:1, 9:2, 12:2). Asserted as an inequality — UTC bucketing must produce a *different* answer. This is the one test that fails loudly if tz handling regresses. |
| Empty account | acct 20 → 200, 25 complete buckets, `insufficient_history`, no exception |
| Verdict calibration | acct 8 (7 vs λ=10.4) → **normal**, not a −36% alarm; acct 6 spike week → **above normal** |

**Layering, so "tests that run" is literally true:** pure-logic tests (bucketing, bands, verdicts,
window completeness) have **zero infrastructure** and always run. Integration tests use
Testcontainers against real SQL Server and are skipped with a clear message when Docker is absent.
A reviewer without Docker still gets a green suite that covers the arithmetic.

### End-to-end verification

1. `docker compose up -d` (SQL Server) → `dotnet ef database update` → import seed.
2. `dotnet test` — unit suite green; integration suite green with Docker, skipped without.
3. `GET /api/accounts/1/pulse` → 53 for the week of 2026-07-20, six location rows, period labelled.
4. `GET /api/accounts/6/pulse?week=2026-06-01` → 880, **above normal**, several locations flagged.
5. `GET /api/accounts/20/pulse` → 200, empty state.
6. In the SPA: pick account 6, navigate to `2026-06-01`, mark a location reviewed with a note,
   **hard-reload** → the note is still there. Navigate away and back → still there.
7. Cross-check any figure on screen against `python tools/profile_seed.py --dedupe`.

---

## 7. Explicitly deferred

**Out of scope per the ticket/constraints:** auth (account chosen from a dropdown), alerting and
notifications, ML/forecasting, infrastructure and CI, visual polish.

**Deliberately cut by me, with reasons:**

| Deferred | Why |
|---|---|
| Charts / sparklines | With Fano≈1 there is no trend to read. A 25-week sparkline would invite customers to find meaning in noise — actively harmful here, not merely expensive. |
| Per-event-type anomaly detection | Triples the multiple-comparisons problem on already-small counts. Needs the §2.2 answer first. |
| Seasonality, holiday calendars, day-of-week modelling | Mon–Sun buckets already absorb the weekday effect (weekend runs at 27% of a weekday). Nothing in 26 weeks supports more. |
| Peer/industry benchmarking | Different product; consent and cohort-sizing problems. |
| Materialised rollups / incremental aggregation | 12.6k rows. The range join is instant. Scaling path noted in the README. |
| Per-location timezones | Schema cannot express it (§2.7). |
| Pagination, i18n, full a11y audit | Max 15 locations. Basic semantic HTML and labels only. |

---

## Where I disagree

**With the ticket — "probably involves comparing against some baseline" invites the wrong build.**
Read naturally that means a trend line against an average, which is precisely what this data cannot
support: the variation is arrival randomness, and a chart would show customers 25 weeks of noise
and let them draw stories from it. The right answer shows *less* data — a verdict, a typical range,
and silence when nothing is happening. I'm building the baseline product asks for; I want it on
record that the deliverable is deliberately quieter than the ticket implies, and that a page which
says "normal" most weeks is the feature working, not an empty state.

**With the ticket — "Monday morning" has an operational dependency nobody has named.** The promise
only holds if ingestion has completed through Sunday night in each account's timezone. In this seed
the newest bucket is one day old and the data is 2 months stale. If the real pipeline lags, the
page will confidently show a week-old verdict labelled as current. The period must always be
rendered as an explicit date range, and pipeline freshness is a prerequisite product hasn't costed.

**With the ticket — dropping alerting caps the value.** Fine for this slice, but a customer only
benefits if they log in on the right morning. The most valuable version of this feature is an email
that says "Site C was unusual last week." Review state is worth building because it is the part
that survives the arrival of alerting, not because it substitutes for it.

**With you — on dedupe, you're right, and the view has a limit worth naming now.** Your reasoning
held and the swing is larger than estimated (up to 50%, median 12%). Two caveats. A SQL Server view
with `GROUP BY` cannot be indexed, so this pattern does not survive contact with millions of rows —
production should reject duplicates at ingest with a unique constraint on the natural key, and the
view is the right call *for this slice only*. And a view silently hides a data-quality problem:
whatever produces those 12 rows keeps producing them. The README should say so.

**With you — on thresholds, I'd extend your correction further than you took it.** You said
thresholds must hold at location grain. Agreed, and the consequence is stronger than picking a
different number: *any* fixed threshold, percentage or z-score, is the wrong instrument, because
required sensitivity scales as 1/√λ across a 10× range of location sizes in a single account. So
I'm not shipping a tunable threshold at all — which is also why I'd push back if the persisted
input were the sensitivity slider. Asking the customer to calibrate is asking them to solve the
problem we were hired to solve.

**With you — the default week is the least interesting week in the dataset.** It flags 2 accounts
and 4 locations; `2026-06-01` flags 22. A reviewer opening the app at its default lands on the
quietest view and may read correct behaviour as a broken feature. This is why week navigation is P1
rather than a nice-to-have, and why the README opens with a link straight to account 6 at
`2026-06-01`. It also gives review state more than one week to live in, which is what stops the
persisted input looking like a toy.
