#!/usr/bin/env python3
"""Cross-check a running /pulse API against the independent oracles (DASH-247, T3).

Every expected value is computed here, at run time, by tools/oracle_poisson.py (which builds on
tools/profile_seed.py): nothing is hard-coded except the HTTP contract, which comes from the rules
in CLAUDE.md. The case list mirrors the integration tests in tests/RelayPulse.Tests/Integration.

Prints one row per check: case · oracle · api · MATCH/MISMATCH. Exits 1 on any mismatch, 2 if the
API is unreachable.

Usage:
  python tools/verify_aggregates.py                          # http://localhost:5117
  python tools/verify_aggregates.py --base-url http://localhost:8080
"""
from __future__ import annotations

import argparse
import json
import math
import sys
import urllib.error
import urllib.request
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import oracle_poisson as op  # noqa: E402

LAMBDA_DP = 4  # the oracle and the C# agree far closer than this; 4 dp keeps the table readable


class Api:
    def __init__(self, base_url: str):
        self.base = base_url.rstrip("/")
        self._cache: dict[str, tuple[int, dict]] = {}

    def get(self, account: int, week: str | None = None) -> tuple[int, dict]:
        url = f"{self.base}/api/accounts/{account}/pulse" + (f"?week={week}" if week else "")
        if url not in self._cache:
            try:
                with urllib.request.urlopen(url, timeout=30) as r:
                    self._cache[url] = (r.status, json.load(r))
            except urllib.error.HTTPError as e:
                body = e.read()
                self._cache[url] = (e.code, json.loads(body) if body else {})
        return self._cache[url]

    def pulse(self, account: int, week: str | None = None) -> dict:
        status, body = self.get(account, week)
        if status != 200:
            raise AssertionError(f"HTTP {status} for account {account} week {week}: {body}")
        return body


def location(body: dict, name: str) -> dict:
    return next(r for r in body["locations"] if r["location"] == name)


def lam(value) -> str:
    return "null" if value is None else f"{value:.{LAMBDA_DP}f}"


def band(value) -> str:
    if value is None:
        return "null"
    lo, hi = (value["lo"], value["hi"]) if isinstance(value, dict) else value
    return f"[{lo},{hi}]"


def flagged(verdict: str) -> bool:
    return verdict in ("above", "below")


class Checker:
    def __init__(self):
        self.rows: list[tuple[str, str, str, bool]] = []

    def eq(self, case: str, oracle, api) -> None:
        self.rows.append((case, str(oracle), str(api), str(oracle) == str(api)))

    def ne(self, case: str, oracle, api) -> None:
        """Passes when the API does NOT serve the oracle's value (e.g. the raw-table count)."""
        self.rows.append((case, f"≠ {oracle}", str(api), str(oracle) != str(api)))

    def row(self, case: str, oracle_row: dict, api_row: dict) -> None:
        """count, verdict, λ and band of one series."""
        self.eq(f"{case} count", oracle_row["count"], api_row["count"])
        self.eq(f"{case} verdict", oracle_row["verdict"], api_row["verdict"])
        self.eq(f"{case} λ", lam(oracle_row["lambda"]), lam(api_row["lambda"]))
        self.eq(f"{case} band", band(oracle_row["band"]), band(api_row["band"]))

    def render(self) -> str:
        headers = ("case", "oracle", "api", "result")
        body = [(c, o, a, "MATCH" if ok else "MISMATCH") for c, o, a, ok in self.rows]
        widths = [max(len(h), *(len(r[i]) for r in body)) for i, h in enumerate(headers)]
        line = lambda cells: "  ".join(c.ljust(w) for c, w in zip(cells, widths)).rstrip()  # noqa: E731
        return "\n".join([line(headers), line(["-" * w for w in widths]), *map(line, body)])

    @property
    def mismatches(self) -> int:
        return sum(not ok for *_, ok in self.rows)


def run(api: Api, seed: op.Seed) -> Checker:
    c = Checker()
    D = date.fromisoformat

    # Dedupe_ReadsViewNotRawTable
    w = "2026-02-23"
    served_e = location(api.pulse(6, w), "Site E")
    c.row("Dedupe acct 6 Site E 2026-02-23", seed.pulse(6, D(w))["locations"]["Site E"], served_e)
    c.ne("Dedupe acct 6 Site E 2026-02-23 raw-table count (must differ)",
         seed.raw_by_loc[(6, "Site E", D(w))], served_e["count"])

    # Totals_LastCompleteWeek
    for a in (1, 6, 8, 18, 19):
        o, b = seed.pulse(a, None), api.pulse(a)
        c.eq(f"Totals acct {a} default week", o["week"].isoformat(), b["period"]["weekStart"])
        c.row(f"Totals acct {a} total", o["total"], b["account"])

    # PerLocation_Account1
    o, b = seed.pulse(1, None), api.pulse(1)
    c.eq("PerLocation acct 1 locations", sorted(o["locations"]), sorted(r["location"] for r in b["locations"]))
    for loc, row in o["locations"].items():
        c.row(f"PerLocation acct 1 {loc}", row, location(b, loc))

    # Spike_DetectedInOwnWeek / Spike_DoesNotPoisonNextWeek
    for label, w in (("Spike own week", "2026-06-01"), ("Spike next week", "2026-06-08")):
        o, b = seed.pulse(6, D(w)), api.pulse(6, w)
        c.row(f"{label} acct 6 {w} total", o["total"], b["account"])
        above = lambda verdicts: sum(v == "above" for v in verdicts)  # noqa: E731
        c.eq(f"{label} acct 6 {w} locations above",
             f"{above(r['verdict'] for r in o['locations'].values())}/{len(o['locations'])}",
             f"{above(r['verdict'] for r in b['locations'])}/{len(b['locations'])}")

    # ZeroFill_SilentLocationWeekAppears
    w = "2026-04-13"
    c.row(f"ZeroFill acct 6 Site G {w}", seed.pulse(6, D(w))["locations"]["Site G"],
          location(api.pulse(6, w), "Site G"))

    # ZeroFill_EmptyWeekCountsAsZeroInBaseline (window includes Site G's empty 2026-04-13)
    w = "2026-06-29"
    c.row(f"ZeroFill baseline acct 6 Site G {w}", seed.pulse(6, D(w))["locations"]["Site G"],
          location(api.pulse(6, w), "Site G"))

    # Baseline_InsufficientHistory
    w = "2026-03-23"
    o, b = seed.pulse(18, D(w)), api.pulse(18, w)
    c.row(f"InsufficientHistory acct 18 {w} total", o["total"], b["account"])
    c.eq(f"InsufficientHistory acct 18 {w} baselineWeeks", len(seed.complete[18][:seed.complete[18].index(D(w))]),
         b["account"]["baselineWeeks"])

    # Ranking_FlaggedAboveUnflagged + acct 15 default week
    for a, w in ((15, None), (6, "2026-06-08"), (6, "2026-04-13")):
        o, b = seed.pulse(a, D(w) if w else None), api.pulse(a, w)
        tag = f"Ranking acct {a} {o['week']}"
        c.eq(f"{tag} order", " > ".join(o["ranking"]), " > ".join(r["location"] for r in b["locations"]))
        verdicts = [r["verdict"] for r in b["locations"]]
        first_unflagged = next((i for i, v in enumerate(verdicts) if not flagged(v)), len(verdicts))
        c.eq(f"{tag} flagged before unflagged", True, not any(flagged(v) for v in verdicts[first_unflagged:]))
    o, b = seed.pulse(15, None), api.pulse(15)
    c.eq("Ranking acct 15 default week: Site B rank", o["ranking"].index("Site B") + 1,
         [r["location"] for r in b["locations"]].index("Site B") + 1)

    # Contract (expected values are the rules in CLAUDE.md, not oracle numbers)
    status, body = api.get(20)
    o = seed.pulse(20, None)
    c.eq("Contract acct 20 status", 200, status)
    c.eq("Contract acct 20 verdict", o["total"]["verdict"], body.get("account", {}).get("verdict"))
    c.eq("Contract acct 20 count", o["total"]["count"], body.get("account", {}).get("count"))
    c.eq("Contract acct 20 period.weekStart", o["week"].isoformat(), (body.get("period") or {}).get("weekStart"))
    for label, (a, w), code, title in (
        ("acct 99", (99, None), 404, "pulse.account_not_found"),
        ("partial week 2026-07-27", (1, "2026-07-27"), 400, "pulse.invalid_week"),
        ("not Monday 2026-07-22", (1, "2026-07-22"), 400, "pulse.invalid_week"),
    ):
        status, body = api.get(a, w)
        c.eq(f"Contract {label} status", code, status)
        c.eq(f"Contract {label} title", title, body.get("title"))

    # Verdict wire format and null band, over every row served above.
    served = [r for (s, b) in api._cache.values() if s == 200 for r in [b["account"], *b["locations"]]]
    allowed = {"normal", "above", "below", "not_enough_volume", "insufficient_history"}
    c.eq("Contract verdicts are snake_case strings", True, all(r["verdict"] in allowed for r in served))
    gated = [r for r in served if r["verdict"] == "not_enough_volume"]
    c.eq("Contract not_enough_volume rows seen", True, len(gated) > 0)
    c.eq("Contract band null when not_enough_volume", True, all(r["band"] is None for r in gated))
    return c


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-url", default="http://localhost:5117")
    ap.add_argument("--db-dir", default="db", type=Path)
    args = ap.parse_args()

    api = Api(args.base_url)
    try:
        api.get(1)
    except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
        print(f"API not reachable at {args.base_url}: {e}", file=sys.stderr)
        return 2

    checker = run(api, op.Seed(args.db_dir))
    print(checker.render())
    print()
    total = len(checker.rows)
    print(f"{total - checker.mismatches}/{total} MATCH, {checker.mismatches} MISMATCH")
    return 1 if checker.mismatches else 0


if __name__ == "__main__":
    sys.exit(main())
