# ai-log

## Sessions

Claude Code sessions in chronological order. Readable transcripts will be in
`transcripts/<session-id>.md`, unedited originals in `raw/<session-id>.jsonl`.
Times are America/Bogota (UTC-5).

| # | Session ID | Date | Phase | Commits |
|---|---|---|---|---|
| 01 | `d7486560-6acc-4665-90c6-35b26df35cff` | 2026-09-26 | Data profiling (`tools/profile_seed.py`) + planning → `PLAN.md` rev 1 and rev 2, `CLAUDE.md` | `e61943a`, `94bf413`, `80dd278`, `c93e15c` |
| 02 | `937226c2-ee17-4588-90c2-7ff1390d7af5` | 2026-09-28 | T0–T1: conventions extracted from my VerusLLC repo into `CLAUDE.md`, scaffold, migrations, dedupe view, week_buckets, seed loader | `48b55d0`, `7d0016d`, `90f63c2` |
| 03 | `42552680-bb73-4784-8d48-2c36a24a92e5` | 2026-09-28 | T2: Poisson tails, trimmed-mean baseline, bands and verdicts in Core (tests first); /pulse aggregation query + endpoint; aggregation-reviewer subagent pass (goldens moved to the checked-in oracle, location-list and empty-state fixes, null band); integration tests deferred to T3 | `<hash>` |
| 04 | `8a026810-cf9a-49e9-aa45-a2c9fdbb4eac` | 2026-09-28 | T3: Testcontainers integration tests for /pulse (one shared SQL Server, real seed loader), `tools/verify_aggregates.py` live cross-check, oracle extended (rank key, raw-vs-deduped, `--series`); mutation check (dedupe view, is_complete, zero-fill) closed a zero-fill gap; stash draft salvaged and dropped | `71db85c`, `7cfc03f`, `c218fc4`, `5624246`, `15c6697` |
| 05 | `3f42f924-5440-41c2-b2b7-8a67f9dfccbe` | 2026-09-28 | T4: Angular SPA in web/ (standalone, signals); URL as the single source of state (/accounts/:id/week/:week, canonical redirect, back button); plain-language verdicts ("1 in N", no p-values); states for empty, not found, insufficient history, week not available; GET /api/accounts + window-edge contract tests; F5 reload and new-window checks verified manually | `<hash>` |

Agent context authored for this task: `CLAUDE.md`.

Decision attribution: lines in `decisions.md` are prefixed `[me]` or `[agent]`.

## Deferred (carry into the project README)

- **Log-p tie-breaking in ranking.** `RankKey = min(p_low/α_low, p_high/α_high)` uses raw
  probabilities, which underflow to 0 below ~1e-308 (e.g. P(X≥880 | λ=72.1)). Several extreme
  flags then tie at 0 and are ordered by location name instead of by severity. Flagged rows still
  always rank above unflagged ones. Fix: rank on log p. Negligible on the seed.
