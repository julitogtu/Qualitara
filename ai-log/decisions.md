# Decisions log

Times: America/Bogota (UTC-5). Format: time · ACCEPTED|REJECTED|REDIRECTED · what · why · ref


2026-09-26 10:19 · ACCEPTED · Profile seed via in-memory SQLite (tools/profile_seed.py) before planning DASH-247 · 2.4MB seed can't be read directly; needed ground truth on tz bucketing, baseline noise and planted anomalies · tools/profile_seed.py

2026-09-26 10:31 · REDIRECTED · Dedupe the 12 exact duplicates via a DB view, not "document and move on" · location-weeks hold 2-6 events, so one dup is up to a 50% swing (median 12%); aggregate correctness is the deliverable · tools/profile_seed.py --dedupe
2026-09-26 10:31 · ACCEPTED · Partial weeks never compared or used in baselines; default view = last COMPLETE week (2026-07-20) · window opens Sun 2026-02-01 and closes Mon 2026-07-27, so both edge buckets are 1/7 days for all 20 accounts · profile_seed.Window
2026-09-26 10:31 · REDIRECTED · Acct-6 2026-06-03 burst recorded as an OPEN QUESTION, not a backfill finding · shape fits bulk import and real demand equally; nothing in the data distinguishes them, so the baseline must be robust either way · profile_seed section 8
2026-09-26 10:40 · DECIDED · SQL Server in Docker over SQLite
2026-09-26 10:51 · ACCEPTED · Quasi-Poisson significance band (lambda +/- 2.576*sqrt(phi*lambda)) as the "normal" test, not % change or median/MAD · weekly counts are Poisson (Fano 0.93 median at both grains), so spread scales as sqrt(lambda) and any fixed threshold is wrong across a 10x range of location sizes · PLAN.md S1
2026-09-26 10:51 · ACCEPTED · Band calibrated to 99% by target false-alarm rate, not convention · at 95% a 15-location account gets ~1 false flag/week; at 99% ~1/month while still catching 16/22 spike-week locations · PLAN.md S1
2026-09-26 10:51 · ACCEPTED · SQL Server + precomputed week_buckets dimension generated via .NET TimeZoneInfo, not AT TIME ZONE · AT TIME ZONE wants Windows tz names but seed is IANA; the dimension is portable, DST-correct and materialises is_complete as a column · PLAN.md S4
2026-09-26 10:51 · ACCEPTED · Week navigation promoted to P1 · default week (2026-07-20) flags only 2 accounts / 4 locations vs 22 on 2026-06-01, so the landing view alone under-demonstrates the feature · PLAN.md "Where I disagree"
2026-09-26 10:51 · ACCEPTED · PLAN.md delivered; no code or scaffolding written this phase · per explicit scope of the planning phase · PLAN.md
