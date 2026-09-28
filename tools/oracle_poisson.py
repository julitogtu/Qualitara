#!/usr/bin/env python3
"""Independent oracle for the DASH-247 verdict model (T2).

Re-implements, separately from the C# in src/RelayPulse.Core/Statistics, what /pulse should
serve: exact Poisson tails, the trimmed 12-week baseline, the band, the verdict and the rank key,
over the dedupe view in each account's local Mon-Sun weeks. Loading, timezone handling and window
completeness come from profile_seed.py, which is the oracle for everything upstream of this.

Deliberately different algorithms from the C#, so agreement means something:
  - tails: per-term exp(k ln λ - λ - lgamma(k+1)) summed with math.fsum
    (C#: log-space recurrence, direct upper-tail sum past the mean)
  - band: linear scan over n (C#: binary search)

Usage:
  python tools/oracle_poisson.py                         # golden values quoted in CLAUDE.md
  python tools/oracle_poisson.py --account 6 --week 2026-06-01
"""
from __future__ import annotations

import argparse
import math
import sys
from collections import Counter
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

sys.path.insert(0, str(Path(__file__).resolve().parent))
import profile_seed as ps  # noqa: E402

ALPHA_LOW = 0.025
ALPHA_HIGH = 0.005
MIN_LAMBDA = 3.7
WINDOW = 12
MIN_WEEKS = 8
# Synthetic λ values (not derived from seed data) used by the C# unit tests to pin the math.
GOLDEN_BAND_LAMBDAS = (3.0, 6.0, 25.0, 75.0)
REFERENCE_LOWER = ((2, 3.0), (0, 3.68), (0, 3.69), (40, 75.0), (400, 500.0), (0, 500.0))
REFERENCE_UPPER = ((9, 3.0), (14, 6.0), (600, 500.0), (60, 20.0))


def pmf(k: int, lam: float) -> float:
    if lam == 0.0:
        return 1.0 if k == 0 else 0.0
    return math.exp(k * math.log(lam) - lam - math.lgamma(k + 1))


def p_low(n: int, lam: float) -> float:
    """P(X <= n)."""
    return min(1.0, math.fsum(pmf(k, lam) for k in range(n + 1))) if n >= 0 else 0.0


def p_high(n: int, lam: float) -> float:
    """P(X >= n), summed directly over a range wide enough to be exhaustive."""
    if n <= 0:
        return 1.0
    stop = int(max(n, lam) + 50 * math.sqrt(lam + 1) + 100)
    return min(1.0, math.fsum(pmf(k, lam) for k in range(n, stop)))


def band(lam: float) -> tuple[int, int]:
    lo = 0
    while p_low(lo, lam) < ALPHA_LOW:
        lo += 1
    hi = lo
    while p_high(hi + 1, lam) >= ALPHA_HIGH:
        hi += 1
    return lo, hi


def baseline(prior: list[int]) -> float | None:
    """Trailing 12 of the prior complete weeks (oldest first); drop one max, one min, mean."""
    if len(prior) < MIN_WEEKS:
        return None
    window = sorted(prior[-WINDOW:])
    trimmed = window[1:-1]
    return sum(trimmed) / len(trimmed)


def rank_key(n: int, lam: float | None, v: str) -> float:
    """min(p_low/α_low, p_high/α_high) for judged rows; +∞ for insufficient history or low volume."""
    if v in ("insufficient_history", "not_enough_volume"):
        return math.inf
    return min(p_low(n, lam) / ALPHA_LOW, p_high(n, lam) / ALPHA_HIGH)


def verdict(n: int, lam: float | None) -> str:
    if lam is None:
        return "insufficient_history"
    if lam < MIN_LAMBDA:
        return "not_enough_volume"
    if p_low(n, lam) < ALPHA_LOW:
        return "below"
    if p_high(n, lam) < ALPHA_HIGH:
        return "above"
    return "normal"


class Seed:
    def __init__(self, db_dir: Path):
        conn = ps.load_db(db_dir)
        self.accounts = {r["id"]: dict(r) for r in conn.execute("SELECT * FROM accounts")}
        events = [dict(r) for r in conn.execute("SELECT * FROM activity_events_dedup")]
        utc = [ps.parse_ts(e["occurred_at"]) for e in events]
        lo, hi = min(utc), max(utc)
        self.complete: dict[int, list[date]] = {}
        self.total: dict[int, Counter] = {a: Counter() for a in self.accounts}
        self.by_loc: dict[int, dict[str, Counter]] = {a: {} for a in self.accounts}
        tzs = {a: ZoneInfo(r["timezone"]) for a, r in self.accounts.items()}
        for a, tz in tzs.items():
            self.complete[a] = ps.Window(lo.astimezone(tz).date(), hi.astimezone(tz).date()).complete
        self.utc_total: dict[int, Counter] = {a: Counter() for a in self.accounts}
        self.null_outcome: dict[int, Counter] = {a: Counter() for a in self.accounts}
        for e, t in zip(events, utc):
            a = e["account_id"]
            w = ps.week_start(t.astimezone(tzs[a]).date())
            self.total[a][w] += 1
            self.by_loc[a].setdefault(e["location"], Counter())[w] += 1
            self.utc_total[a][ps.week_start(t.date())] += 1  # the wrong bucketing, for contrast only
            if e["outcome"] is None:
                self.null_outcome[a][w] += 1
        # The raw table, same bucketing: only to show what reading past the dedupe view would serve.
        self.raw_by_loc: Counter = Counter()
        for e in conn.execute("SELECT account_id, location, occurred_at FROM activity_events"):
            a = e["account_id"]
            w = ps.week_start(ps.parse_ts(e["occurred_at"]).astimezone(tzs[a]).date())
            self.raw_by_loc[(a, e["location"], w)] += 1

    def judge(self, series: Counter, weeks: list[date], week: date) -> dict:
        i = weeks.index(week)
        prior = [series.get(w, 0) for w in weeks[:i]]
        n = series.get(week, 0)
        lam = baseline(prior)
        v = verdict(n, lam)
        return {"count": n, "lambda": lam, "verdict": v, "rank_key": rank_key(n, lam, v),
                "band": band(lam) if lam is not None and lam >= MIN_LAMBDA else None}

    def pulse(self, account: int, week: date | None) -> dict:
        weeks = self.complete[account]
        week = week or weeks[-1]
        if week not in weeks:
            raise SystemExit(f"{week} is not a complete week for account {account}")
        acct = self.judge(self.total[account], weeks, week)
        if not self.total[account]:
            acct = {"count": 0, "lambda": None, "verdict": "insufficient_history", "band": None,
                    "rank_key": math.inf}
        locs = {loc: self.judge(c, weeks, week) for loc, c in sorted(self.by_loc[account].items())}
        # Ranked order the API must serve: rank key ascending, ties by location label.
        ranking = sorted(locs, key=lambda loc: (locs[loc]["rank_key"], loc))
        return {"account": account, "week": week, "total": acct, "locations": locs, "ranking": ranking}


def fmt(row: dict) -> str:
    lam = "-" if row["lambda"] is None else f"{row['lambda']:.4g}"
    b = "-" if row["band"] is None else f"[{row['band'][0]},{row['band'][1]}]"
    return f"n={row['count']:<4} λ={lam:<7} band={b:<9} {row['verdict']}"


def print_pulse(p: dict) -> None:
    print(f"account {p['account']} week {p['week']}: {fmt(p['total'])}")
    for rank, loc in enumerate(p["ranking"], 1):
        row = p["locations"][loc]
        print(f"  #{rank:<2} {loc:<8} {fmt(row)}  rank_key={row['rank_key']:.4g}")


def print_series(seed: Seed, account: int) -> None:
    """Weekly totals over every complete week: account-local (served) vs UTC (wrong), NULL outcomes."""
    weeks = seed.complete[account]
    local = [seed.total[account].get(w, 0) for w in weeks]
    print(f"account {account} ({seed.accounts[account]['timezone']}), {len(weeks)} complete weeks:")
    for w, n in zip(weeks, local):
        u = seed.utc_total[account].get(w, 0)
        flag = "  <- differs under UTC" if u != n else ""
        print(f"  {w}  local={n:<4} utc={u:<4} null_outcome={seed.null_outcome[account].get(w, 0)}{flag}")
    print(f"  series: {local}")
    print(f"  min={min(local)} max={max(local)} median={sorted(local)[len(local) // 2]}")


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db-dir", default="db", type=Path)
    ap.add_argument("--account", type=int)
    ap.add_argument("--week", type=date.fromisoformat)
    ap.add_argument("--series", action="store_true", help="with --account: weekly totals, local vs UTC")
    args = ap.parse_args()

    seed = Seed(args.db_dir)
    if args.account is not None and args.series:
        print_series(seed, args.account)
        return 0
    if args.account is not None:
        print_pulse(seed.pulse(args.account, args.week))
        return 0

    print("Reference tails (synthetic λ; PoissonTailTests):")
    for n, lam in REFERENCE_LOWER:
        print(f"  P(X<={n} | λ={lam:g}) = {p_low(n, lam)!r}")
    for n, lam in REFERENCE_UPPER:
        print(f"  P(X>={n} | λ={lam:g}) = {p_high(n, lam)!r}")
    print()
    print("Bands (synthetic λ → counts judged normal; BandTests):")
    for lam in GOLDEN_BAND_LAMBDAS:
        print(f"  λ={lam:g} → {list(band(lam))}")
    print(f"n=0 flips to below at λ = ln(1/α_low) = {math.log(1 / ALPHA_LOW):.4f}")
    print()
    print("Baselines from real weekly series (BaselineTests):")
    for a, w in ((6, date(2026, 6, 8)), (8, date(2026, 7, 20))):
        weeks = seed.complete[a]
        i = weeks.index(w)
        prior = [seed.total[a].get(x, 0) for x in weeks[i - WINDOW:i]]
        lam = baseline(prior)
        print(f"  account {a}, 12 weeks {weeks[i - WINDOW]} … {weeks[i - 1]}: {prior}")
        print(f"    trimmed λ = {lam:.4g} (plain mean {sum(prior) / len(prior):.4g}), band {list(band(lam))}")
    print()
    print("Default week, per account:")
    for a in (1, 6, 8, 18, 19, 20):
        print(f"  {a:>2}: {fmt(seed.pulse(a, None)['total'])}")
    print()
    print_pulse(seed.pulse(1, None))
    print()
    for w in (date(2026, 6, 1), date(2026, 6, 8)):
        p = seed.pulse(6, w)
        above = sum(r["verdict"] == "above" for r in p["locations"].values())
        print(f"account 6 week {w}: {fmt(p['total'])}  ({above}/{len(p['locations'])} locations above)")
    loc_e = seed.by_loc[6]["Site E"][date(2026, 2, 23)]
    raw_e = seed.raw_by_loc[(6, "Site E", date(2026, 2, 23))]
    print(f"account 6 Site E week 2026-02-23: raw {raw_e} → deduped {loc_e}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
