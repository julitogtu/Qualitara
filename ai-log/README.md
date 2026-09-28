# ai-log

## Sessions

Claude Code sessions in chronological order. Readable transcripts will be in
`transcripts/<session-id>.md`, unedited originals in `raw/<session-id>.jsonl`.
Times are America/Bogota (UTC-5).

| # | Session ID | Date | Phase | Commits |
|---|---|---|---|---|
| 01 | `d7486560-6acc-4665-90c6-35b26df35cff` | 2026-09-26 | Data profiling (`tools/profile_seed.py`) + planning → `PLAN.md` rev 1 and rev 2, `CLAUDE.md` | `e61943a`, `94bf413`, `80dd278`, `c93e15c` |
| 02 | `937226c2-ee17-4588-90c2-7ff1390d7af5` | 2026-09-28 | T0–T1: conventions extracted from my VerusLLC repo into `CLAUDE.md`, scaffold, migrations, dedupe view, week_buckets, seed loader | `48b55d0`, `7d0016d`, `90f63c2` |

Agent context authored for this task: `CLAUDE.md`.

Decision attribution: lines in `decisions.md` are prefixed `[me]` or `[agent]`.

## Deferred (carry into the project README)

- **Log-p tie-breaking in ranking.** `RankKey = min(p_low/α_low, p_high/α_high)` uses raw
  probabilities, which underflow to 0 below ~1e-308 (e.g. P(X≥880 | λ=72.1)). Several extreme
  flags then tie at 0 and are ordered by location name instead of by severity. Flagged rows still
  always rank above unflagged ones. Fix: rank on log p. Negligible on the seed.
