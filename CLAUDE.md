# Relay Pulse — DASH-247

Dashboard feature answering "is this activity normal for us?" per account and per location.
Full reasoning in `PLAN.md`. Data evidence in `tools/profile_seed.py`. This file is the
short version: the rules that must hold in code.

## Status

Planning done, **nothing implemented yet**. Repo currently holds `db/`, `docs/`,
`tools/profile_seed.py`, `ai-log/`, `PLAN.md`. Treat the stack below as the target.

## Stack

| Layer | Choice |
|---|---|
| Backend | .NET 10 Web API, EF Core 10 (migrations — never `EnsureCreated`) |
| DB | SQL Server in Docker |
| Reads | Raw parameterised SQL via `Database.SqlQueryRaw<T>` for the aggregation; EF for writes |
| Frontend | Angular 21 standalone components + signals. **No NgRx** |
| Tests | xUnit; Testcontainers for integration only |
| Tooling | Python 3.10 — `tools/profile_seed.py` (the data oracle) |

## Working rules

**Never read `db/seed.sql` in full.** 2.4MB, ~12,600 INSERTs. It will blow out context and tell
you nothing a query won't. To inspect data, run `python tools/profile_seed.py [--dedupe]`, or
load `schema.sql` + `seed.sql` into in-memory SQLite and query it (see `load_db()` in that file).
`head`/`grep` on it is fine.

**Never edit `db/seed.sql` or `db/schema.sql`.** They are the fixture. Dedupe and reshape happen
in migrations and views, never by rewriting the seed.

**No `DateTime.UtcNow` / `DateTime.Now` / `Date.now()` in domain, query, or view code.** Two
separate reasons, both load-bearing:
- *Correctness*: the dataset ends `2026-07-27`, roughly two months before wall clock. Anything
  keyed off the system clock computes an empty current week. The current period comes from
  `MAX(occurred_at)`, not from now.
- *Testability*: golden-value tests must be stable forever.

Inject an `IClock` where a real timestamp is genuinely needed (review-state `created_at`), and
derive every reporting period from the data. A grep for `UtcNow` outside composition root and
clock implementation should return nothing.

**Tests assert golden values produced by `tools/profile_seed.py --dedupe`** — an independent
implementation, in another language, written before any app code. Do not "fix" a failing test by
copying what the C# returned; re-derive from the profiler and find out which one is wrong.

**Unit tests run with zero infrastructure.** Bucketing, bands, verdicts and window completeness
must not need Docker. Integration tests use Testcontainers and skip with a clear message when
Docker is absent, so `dotnet test` is green either way.

**Log decisions in `ai-log/decisions.md`.** Format is at the top of that file: times in
America/Bogota, `time · ACCEPTED|REJECTED|REDIRECTED · what · why · ref`.

## Domain rules

These come from profiling the seed. Violating one produces plausible, wrong numbers.

**Time**
- `occurred_at` is UTC. Bucket in the **account's own IANA timezone**, Mon–Sun weeks.
- Never bucket in UTC. 8 events land in a different week under UTC — small, and exactly the
  regression that will otherwise ship silently.
- Six zones in play, including `America/Phoenix` (no DST) and a literal `UTC`.
- A week is **complete** only when all 7 days fall inside the observation window. Completeness is
  a property of the window, not of where events land — a week with zero events can be complete.
- **Partial weeks are never compared, never used in a baseline, and never the default view.**
  Both edge buckets (`2026-01-26`, `2026-07-27`) cover 1/7 days for all 20 accounts.
- Default view is the **last complete week**: `2026-07-20 … 2026-07-26`.

**Identity**
- Location key is `(account_id, location)`. Labels are generic (`Site A`…`Site O`) and reused
  across accounts — account 1's Site A is not account 6's Site A.
- `accounts.created_at` is **not** the start of history. Every account's events start `2026-02-01`
  regardless. Never window on `created_at`.
- One timezone per account; the schema cannot express per-location zones (known limitation).

**Reads**
- All reporting reads go through the **dedupe view**, never the raw table. 12 exact duplicate rows
  (7 accounts, 11 location-weeks) swing an affected location-week by 1–50%, median 12%.

**Statistics**
- Weekly counts are Poisson (Fano ≈ 0.93 at both grains). Spread scales as √λ.
- Use the quasi-Poisson band `λ ± 2.576·√(φλ)`, φ clamped `[1,4]`. **Never a percentage threshold
  or a fixed z-score** — required sensitivity varies ~10× across locations in one account.
- 2.576 is calibrated to ~1 false flag per month for a 15-location account, not picked by
  convention. Changing it changes that rate; see the table in `PLAN.md` §1.
- Baseline = trailing 12 complete weeks, min 8, **excluding detected anomaly weeks**.
- Below λ = 3/week report `not enough volume`, do not flag. 49 of 69 locations average <8/week.

**Fields**
- `outcome` is a **per-type** enum, 7 values, not the 5 in `docs/`: calls →
  `connected|missed|voicemail`, appointments → `completed|no_show`, leads → `converted|open`.
- `outcome` NULL is valid (398 rows, 3.2%) — count it, render it as `unspecified`, never drop it.
- `duration_seconds` is noise: calls only, clamped `[20,1500]`, uncorrelated with outcome (missed
  calls median 711s). **Build no metric on it.**
- Hour-of-day is a generator artifact (seed sampled in UTC, never localised). No hour-of-day
  features. Day and week bucketing are unaffected and still required.

**Edge cases that must work**
- Account 20 has zero events → HTTP 200, populated window, `insufficient_history`. Never 404,
  never divide by zero.
- Account 6 week `2026-06-01` is a 12× anomaly of undetermined cause. It must be flagged *and*
  excluded from its own baseline (mean 72.7, not 105.0). Do not "explain" it in code or copy.

## Golden values

From `python tools/profile_seed.py --dedupe`.

| | |
|---|---|
| Events raw → deduped | 12,626 → **12,614** (12 groups, 7 accounts) |
| Buckets per account | 27 total, **25 complete**, last complete `2026-07-20` |
| Account totals, last complete week | 1=**53**, 6=**87**, 8=**7**, 18=**20**, 19=**13**, 20=**0** |
| Account 1 per-location, same week | A=9, B=10, C=9, D=9, E=9, F=7 |
| Account 6 spike week `2026-06-01` | **880**; baseline mean 72.7 excl. / 105.0 incl. |
| Dedupe at grain | acct 6 / Site E / `2026-02-23`: 3 → **2** |
| Timezone sensitivity | exactly **8** events change week under UTC bucketing |
| Verdict calibration | acct 8 (7 vs λ=10.4) → `normal`, not a −36% alarm |

## Commands

```bash
python tools/profile_seed.py              # full profile, raw
python tools/profile_seed.py --dedupe     # what the API should serve
python tools/profile_seed.py --markdown   # markdown tables
```
