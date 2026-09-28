# Relay Pulse — DASH-247

## Quick start (Docker)

**Prerequisites:** Docker with Compose v2. Nothing else is needed; the .NET and Node builds run
inside the images.

```bash
cp .env.example .env               # optional: every value has a dev-only default
docker compose up --build          # sqlserver → seed (one-shot) → api → web
```

Open **<http://localhost:8080/accounts/6/week/2026-06-01>**. The first run takes a few minutes
to build the images and load the seed. `docker compose down -v` stops everything and deletes the
database volume.

How the pieces fit together, with diagrams: [`docs/architecture.md`](docs/architecture.md).

---

Relay Pulse answers "is this activity normal for us?" for each account and each location. The
weekly counts in the seed behave like Poisson arrivals (Fano ≈ 0.93), so most of the week-to-week
movement is just randomness. I didn't build a chart or a percentage threshold. I built a
volume-aware significance test: for each location-week it gives a verdict and the range of counts
that would count as normal, and it says nothing when nothing unusual is happening.

**Start here:** <http://localhost:8080/accounts/6/week/2026-06-01> (Docker) or
<http://localhost:4200/accounts/6/week/2026-06-01> (local dev)

That week, account 6 logged **880** events against an expected **72.1**, and all **15/15**
locations are flagged. The app opens on the last complete week (`2026-07-20`) instead, and that
week is quiet on purpose. Accounts 1, 6, 8, 18 and 19 are all `normal` there. When the page says
"normal" in most weeks, the feature is doing its job. It isn't an empty state.

---

## How to run locally

**Prerequisites:** Docker, the .NET 10 SDK (pinned in `global.json`), Node with npm, and
Python 3.10 (only needed for the oracles in `tools/`).

```bash
cp .env.example .env               # optional: dev-only defaults work as-is
docker compose up -d sqlserver     # only SQL Server 2022 on localhost:1433, with a healthcheck
dotnet run --project src/RelayPulse.Api -- seed   # migrate + load db/seed.sql + build week_buckets (idempotent)
dotnet run --project src/RelayPulse.Api           # API on http://localhost:5117
cd web && npm install && npm start                # SPA on http://localhost:4200, /api proxied to :5117
```

**Environment (`.env.example`):**

| Variable | Default | Purpose |
|---|---|---|
| `MSSQL_SA_PASSWORD` | `DevOnly_Relay_2026!` | SA password for the container. Public, dev only. |
| `MSSQL_PORT` | `1433` | Host port mapped to the container's 1433 |
| `MSSQL_DATABASE` | `RelayPulse` | Database the `seed` and `api` containers use |
| `WEB_PORT` | `8080` | Host port for the `web` container (nginx + `/api` proxy) |
| `ConnectionStrings__Relay` | set in `appsettings.Development.json` (local); built from the values above in compose | Override this for `dotnet run` only if you change the password or port |

**Tests:** `dotnet test`. The Core tests always run. The integration tests need Docker and skip with
a message when it isn't available.

---

## What "normal" means (my reading of DASH-247)

- **Baseline:** the location's own trailing **12 complete weeks** (at least 8, otherwise
  `insufficient_history`). I drop the single highest and single lowest week and take the mean of
  the rest, which gives λ.
- **Test:** exact Poisson tails, with different thresholds for each direction. A week is `below`
  if P(X ≤ n) < **0.025** and `above` if P(X ≥ n) < **0.005**. Drops get the looser threshold
  because they're more likely to need action (phones down, staff out).
- **Band:** the integer counts that would come out `normal`. It's derived from the same tails, so
  the band and the verdict can't disagree. Examples: λ 3 → [0, 8], 6 → [2, 13], 25 → [16, 39],
  75 → [59, 98].
- **Volume gate:** when λ < **3.7**, the verdict is `not enough volume` and the row gets no band.
  3.7 is just above ln 40 = 3.689, the point where an empty week first becomes detectable.
- **Ranking:** rows sort by `min(p_low/0.025, p_high/0.005)`, lowest first. The key is < 1 exactly
  when a row is flagged, so flagged rows always sort first.
- **On screen:** "Unusually high: a week this busy happens by chance less than 1 in 200 weeks."
  The page shows no p-values or z-scores.

**Why not % change.** Poisson spread grows with √λ, so a fixed percentage is too strict for small
locations and too lenient for large ones. Within a single account, the sensitivity you need varies
by about 10×. Account 8 is the worked example: 7 events against λ **10.8** looks like a −36% alarm
(PLAN.md, measured against its median of 11). Its band is **[5, 20]**, so it's an ordinary week and
the verdict is `normal`. A sensitivity slider would hand the calibration problem back to the
customer, so I didn't ship one.

---

## Assumptions (PLAN.md §2)

None of these blocked the build. Each one has a working answer that product can overturn.

| Open question | Working assumption |
|---|---|
| Normal relative to the account's own history, or to peers? | Own history. Peer benchmarking is a separate product. |
| All events, or judge calls / leads / appointments separately? | Flag on total events and show the per-type split per row. Known gap: totals can stay flat while appointments collapse. |
| Should a drop be as prominent as a spike? | Yes. Both are flagged, and drops get the looser α. |
| Should a confirmed-real week (a campaign) stay out of future baselines? | The trim handles it: one extreme week falls out automatically. A sustained step-change becomes the new normal after a few weeks. |
| Mon–Sun weeks or months? | Mon–Sun, to match "Monday morning". |
| Can one account's locations span timezones? | No. The schema only has `accounts.timezone`. |
| Is the 2-month gap between the last event and wall-clock time lag or an export artifact? | I treat it as an artifact. The current period comes from `MAX(occurred_at)`, never from the system clock. |

---

## Data handling

- **Duplicates.** The seed has 12 exact duplicate rows across 7 accounts and 11 location-weeks, so
  12,626 events become **12,614** after dedupe. The `activity_events_dedup` view does it
  (`GROUP BY` every column except `id`, keep `MIN(id)`), and every reporting read goes through the
  view. Account-wide the effect is negligible, but per location-week it swings counts by **1–50%
  (median 12%)**. Example: account 6 / Site E / `2026-02-23` has 3 raw events and 2 after dedupe.
  I never edit the seed.
- **The account 6 spike.** 880 events in `2026-06-01`. I don't know the cause, and nothing in the
  data separates a bulk import from real demand, so the code doesn't try to explain it. The week
  is flagged, and the trim keeps it out of later baselines. For `2026-06-08`, λ is **72.3**; a
  plain mean would give 138.0.
- **Empty account.** Account 20 has no events. It returns HTTP 200 with a populated window and
  `insufficient_history`. It is never a 404 and never divides by zero. An unknown account id is a
  404 (`pulse.account_not_found`).
- **Timezones and DST.** Weeks are account-local Mon–Sun, across 6 IANA zones (including
  `America/Phoenix` with no DST, and a literal `UTC`). I generate a `week_buckets` table in C#
  with `TimeZoneInfo` and range-join events onto it. I avoided SQL Server's `AT TIME ZONE` because
  it expects Windows zone names. Under UTC bucketing, **8** events would land in a different week.
- **Partial weeks.** Each account has 27 buckets. **25** are complete, and both edge buckets cover
  only 1 of 7 days. Completeness is stored as `week_buckets.is_complete`. Partial weeks never get a
  verdict, never enter a baseline, and are never the default view. The API rejects them with
  400 `pulse.invalid_week`.
- **NULL outcomes.** 398 raw rows (397 after dedupe, about 3.2%). They count toward totals and
  appear as `unspecified` in the per-type split. They are never dropped.
- **Low volumes.** 49 of 69 locations average fewer than 8 events a week. The λ ≥ 3.7 gate returns
  `not enough volume` for these instead of generating false alarms. A location that goes silent
  keeps appearing, filled in as n = 0.
- **Global cutoff.** The window ends at the global `MAX(occurred_at)` (`2026-07-27`). The last
  complete week is `2026-07-20 … 2026-07-26`. Nothing reads `DateTime.UtcNow`.

---

## Design decisions and trade-offs

- **SQL Server in Docker**, with a compose healthcheck. The API waits on `service_healthy`.
- **EF Core for the schema, raw SQL for the aggregation.** Migrations (never `EnsureCreated`), the
  dedupe view and the seed loader use EF. The one aggregation query is parameterised SQL through
  `SqlQueryRaw`: a single `GROUPING SETS` statement returns both the account and location grains.
  That query is the product, so I wanted it readable and reviewable, not generated from LINQ.
- **Three projects: Api, Core and Tests.** Tails, bands, baselines, zero-fill and ranking all live
  in Core, so they're unit-tested with no infrastructure. Api holds only the query, validation and
  wire mapping. I left out MediatR, extra layers, versioning and Serilog; the reasons are in
  `ai-log/decisions.md`.
- **The URL is the only UI state.** `/accounts/:id/week/:week` binds to signal inputs, which feed
  `httpResource`. There's no NgRx and no localStorage. The back button works, and a link sent to an
  account manager opens the same week.
- **Plain-language verdicts.** "Unusually high / low", "Not enough volume to judge", "Not enough
  history yet", plus a "1 in N weeks" sentence. N is the largest of {40, 100, 200, 500, 1000} for
  which the sentence is still true.

I adapted the solution structure (`.slnx`, central package management, DI conventions,
ProblemDetails, test style) from my earlier public repo [VerusLLC](TODO-link). The decisions log
records what I kept and what I rejected from it.

---

## How I verified

1. **Independent oracles.** `tools/profile_seed.py` (counts, window, dedupe, timezones) and
   `tools/oracle_poisson.py` (tails, bands, trimmed baselines, verdicts, rank keys) reimplement the
   model in Python, using different algorithms from the C#: per-term `lgamma` + `fsum` against a
   log-space recurrence, and a linear band scan against a binary search. Every golden in the tests
   is printed by one of these scripts. When a test failed, I re-derived the value from the oracle
   and never copied the C# output.
2. **`tools/verify_aggregates.py`** checks the live API against the oracle: **98/98 MATCH, 0
   MISMATCH**. I reran it while writing this README.
3. **Test suite.** `dotnet test` gives **119 passed, 0 skipped** with Docker running.
4. **Mutation check (T3)** on the integration suite:
   - **(a)** Read `activity_events` instead of the dedupe view → **4 tests fail**.
   - **(b)** Drop `is_complete = 1` from the SQL → no failures. This is an equivalent mutant: the
     `@from/@to` bounds and `PulseBuilder` already exclude partial weeks. The SQL filter is now
     marked as a backstop.
   - **(b′)** The same rule where it actually lives (the complete-week list in `PulseEndpoints`)
     → *TODO: confirm the result from the session 04 transcript.*
   - **(c)** Skip empty weeks instead of zero-filling them → **survived**. That gap produced
     `ZeroFill_EmptyWeekCountsAsZeroInBaseline`: account 6 / Site G / `2026-06-29` has the window
     `[2,0,2,6,4,3,7,5,57,4,5,3]`, the trim drops 0 and 57, λ = **4.1**, band [1, 10], and n = 0 →
     `below`.
5. **Series cross-check.** I checked the 25-week series for accounts 1 and 9 against a separate
   script. Account 1 differs from the raw counts in only two weeks (`04-27`: 57 → 56 and `07-06`:
   43 → 42), and those are exactly its two duplicate rows.
6. **Manual check.** A hard reload (F5), and opening `/accounts/6/week/2026-06-01` in a new private
   window, both restore the exact view. Browser back works after changing the account in the
   picker.

---

## Plan corrections found during verification

- **PLAN.md is committed as rev 1.** In the same planning session, before any code, I revised the
  model (rev 2: exact Poisson tails with asymmetric α, trimmed-mean baseline, λ ≥ 3.7 gate, review
  state cut). Rev 2 is in transcript 01 at 2026-09-26 11:08–11:15 (America/Bogota), before the
  first code commit `7d0016d` (2026-09-28 06:28). PLAN.md is kept as written, per the brief.

Verification then corrected two statements in rev 2 (transcript 01, 11:15):

- **Account 6 `2026-06-08` is `above`, not `normal`.** Rev 2 said that with the trimmed λ of 72.3,
  n = 102 comes out normal. The trim itself works: a plain mean of 138.0 would have called the week
  *below*. But **102** is above the band **[56, 95]**, and 1 of 15 locations is flagged.
- **Account 15 / Site B ranks 1st in its account, not "2 of 69".** Rev 2 described a global rank
  across all locations, but the product ranks locations within an account. On the default week
  nothing is flagged. Site B (2 vs λ 6.8, key 1.378) leads Site C (12 vs λ 5.3, key 1.682).

---

## Deferred, and known limitations

**Out of scope per the ticket:** auth, alerting, ML/forecasting, CI and infrastructure, visual
polish.

**Cut deliberately:**

| Deferred | Why |
|---|---|
| Charts and sparklines | With Fano ≈ 1 there's no trend to read, and a 25-week line would invite people to see stories in noise. |
| Per-event-type anomaly detection | It triples the number of comparisons on already-small counts. |
| Seasonality and day-of-week modelling | Mon–Sun buckets already absorb the weekday effect, and 26 weeks can't support more. |
| Quasi-Poisson φ | φ ≈ 1 on this seed, so it would change nothing. It returns if real data turns out overdispersed. |
| Review state (PLAN T5: `PUT …/reviews`, reviewed flag + note) | Cut in rev 2 because nobody asked for it. The persisted input is account + week in the URL. |

**Known limitations:**

- **The dedupe view won't scale.** A `GROUP BY` view can't be indexed, and it hides the upstream
  bug that creates the duplicates. In production I'd reject duplicates at ingest with a unique
  constraint on the natural key.
- **Calibration comes from synthetic data.** The α values and the 3.7 gate fit a seed whose counts
  are almost exactly Poisson. Real series with campaigns or seasonality will be more dispersed.
- **Log-p ties.** The rank key uses raw p-values, which underflow to 0 below about 1e-308 (for
  example P(X ≥ 880 | λ 72.1)). Extreme flags then tie and fall back to name order. Flagged rows
  still always sort above unflagged ones.
- **Per-location timezones.** The schema has one timezone per account, and a 15-location group
  could plausibly span several.
- **Ingestion freshness.** "Monday morning" only holds if ingestion has finished through Sunday
  night in every account's timezone. The page always shows the date range, but nothing checks
  freshness yet.

---

## With another day

1. A Playwright E2E test for the reload check, so the manual F5 check becomes automated.
2. Quasi-Poisson φ, estimated on real data.
3. Dedupe at ingest with a unique constraint on the natural key, instead of the view.
4. Per-type anomaly detection, with a multiplicity correction.
5. Rank on log p so extreme flags order by severity.
6. A weekly email: "Site C was unusual last week." That's the version of this feature customers
   would actually notice.

---

## Process

- **Spec-driven.** PLAN.md §1–3 served as the spec (interpretation, open questions, data
  handling). §6 lists the acceptance criteria as goldens. Tasks T0–T4 ran against it, and
  deviations are logged rather than silently absorbed.
- **Agent context in the repo:**
  - `CLAUDE.md` holds the rules the code must follow: domain rules, golden values, and the
    no-wall-clock rule.
  - `.claude/agents/aggregation-reviewer.md` is a read-only reviewer with a 12-point checklist,
    covering the dedupe view, local weeks, the off-by-one in p_high, and the trim.
  - `.claude/agents/verify-aggregates.md` (`/verify-aggregates`) runs the oracle cross-check and
    reports mismatches without fixing them.
- **AI log.** `ai-log/decisions.md` records every accepted, rejected and redirected decision, with
  `[me]` marking my own calls, including where I overruled the agent. `ai-log/README.md` maps each
  session to its commits.
