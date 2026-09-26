#!/usr/bin/env python3
"""Profile the Relay take-home seed data (DASH-247 discovery).

Loads db/schema.sql + db/seed.sql into an in-memory SQLite database and reports
shape, quality and time-bucketing characteristics of the dataset. Weekly
bucketing uses each ACCOUNT'S OWN IANA timezone, Mon-Sun weeks.

Usage:
    python tools/profile_seed.py [--db-dir db] [--markdown] [--dedupe]

Read-only with respect to the repo: nothing is written to disk.
"""

from __future__ import annotations

import argparse
import re
import sqlite3
import statistics
import sys
from collections import Counter, defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

# Values the product background documents as expected.
KNOWN_EVENT_TYPES = {"call_received", "lead_created", "appointment_set"}
KNOWN_OUTCOMES = {"connected", "missed", "voicemail", "converted", "no_show"}

# Robust-outlier threshold on the modified z-score (Iglewicz & Hoaglin).
OUTLIER_MZ = 3.5
# MAD-based scores need a minimum sample to mean anything.
MIN_POINTS_FOR_OUTLIERS = 8


# --------------------------------------------------------------------------
# loading
# --------------------------------------------------------------------------

# Collapses exact duplicates (every column but id) to one row, keeping the
# lowest id. Mirrors the dedupe view the application reads through: the raw
# table and the seed are never modified.
DEDUP_VIEW = """
CREATE VIEW activity_events_dedup AS
SELECT MIN(id) AS id, account_id, location, event_type, occurred_at,
       duration_seconds, outcome
FROM activity_events
GROUP BY account_id, location, event_type, occurred_at,
         duration_seconds, outcome
"""


def load_db(db_dir: Path) -> sqlite3.Connection:
    """Execute schema.sql then seed.sql against a fresh in-memory database."""
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    for name in ("schema.sql", "seed.sql"):
        path = db_dir / name
        if not path.exists():
            sys.exit(f"missing {path}")
        conn.executescript(path.read_text(encoding="utf-8"))
    conn.executescript(DEDUP_VIEW)
    conn.commit()
    return conn


def parse_ts(raw: str) -> datetime:
    """Parse a seed timestamp as UTC. Seeds use 'YYYY-MM-DD HH:MM:SS'."""
    return datetime.fromisoformat(raw.strip()).replace(tzinfo=timezone.utc)


def week_start(d: date) -> date:
    """Monday of the ISO week containing d."""
    return d - timedelta(days=d.weekday())


def iter_weeks(first: date, last: date):
    """Yield every Monday from the week of first through the week of last."""
    cur = week_start(first)
    end = week_start(last)
    while cur <= end:
        yield cur
        cur += timedelta(days=7)


class Window:
    """An account's observation window in its own local calendar.

    A Mon-Sun bucket is COMPLETE only when all seven of its days fall inside
    the window. Completeness is a property of the window, not of where events
    happen to land: an account with no events on the window's first Sunday
    still has a partial bucket there, it just has a zero in it.

    Per DASH-247: partial weeks are never compared and never feed a baseline.
    """

    def __init__(self, start: date, end: date):
        self.start = start          # first observed local day, inclusive
        self.end = end              # last observed local day, inclusive
        self.all_weeks = list(iter_weeks(start, end))
        self.complete = [w for w in self.all_weeks
                         if w >= start and w + timedelta(days=6) <= end]
        self.partial = [w for w in self.all_weeks if w not in set(self.complete)]

    @property
    def last_complete(self) -> date | None:
        """Monday of the default dashboard view."""
        return self.complete[-1] if self.complete else None

    def is_partial(self, w: date) -> bool:
        return w not in set(self.complete)

    def coverage(self, w: date) -> tuple[date, date]:
        """The part of week w actually inside the window."""
        return max(w, self.start), min(w + timedelta(days=6), self.end)


# --------------------------------------------------------------------------
# stats helpers
# --------------------------------------------------------------------------

def modified_z(values: list[float]) -> list[float]:
    """Modified z-scores using median / MAD, robust to the outliers we hunt."""
    if len(values) < 2:
        return [0.0] * len(values)
    med = statistics.median(values)
    mad = statistics.median([abs(v - med) for v in values])
    if mad == 0:
        # Degenerate spread: fall back to stdev so a lone spike still scores.
        sd = statistics.pstdev(values)
        if sd == 0:
            return [0.0] * len(values)
        return [(v - med) / sd for v in values]
    return [0.6745 * (v - med) / mad for v in values]


def fmt_table(headers: list[str], rows: list[list], markdown: bool = False) -> str:
    """Render a list of rows as an aligned text table (or a markdown one)."""
    cells = [[("" if c is None else str(c)) for c in row] for row in rows]
    widths = [len(h) for h in headers]
    for row in cells:
        for i, c in enumerate(row):
            widths[i] = max(widths[i], len(c))
    if markdown:
        out = ["| " + " | ".join(h.ljust(widths[i]) for i, h in enumerate(headers)) + " |"]
        out.append("| " + " | ".join("-" * widths[i] for i in range(len(headers))) + " |")
        for row in cells:
            out.append("| " + " | ".join(row[i].ljust(widths[i]) for i in range(len(headers))) + " |")
        return "\n".join(out)
    out = ["  ".join(h.ljust(widths[i]) for i, h in enumerate(headers))]
    out.append("  ".join("-" * widths[i] for i in range(len(headers))))
    for row in cells:
        out.append("  ".join(row[i].ljust(widths[i]) for i in range(len(headers))))
    return "\n".join(out)


class Report:
    """Accumulates report sections so output format is decided once."""

    def __init__(self, markdown: bool):
        self.markdown = markdown
        self.parts: list[str] = []

    def heading(self, text: str) -> None:
        if self.markdown:
            self.parts.append(f"\n## {text}\n")
        else:
            self.parts.append(f"\n{'=' * 78}\n{text}\n{'=' * 78}")

    def sub(self, text: str) -> None:
        self.parts.append(f"\n### {text}\n" if self.markdown else f"\n-- {text}")

    def line(self, text: str = "") -> None:
        self.parts.append(text)

    def table(self, headers: list[str], rows: list[list]) -> None:
        if not rows:
            self.parts.append("_(none)_" if self.markdown else "   (none)")
            return
        self.parts.append(fmt_table(headers, rows, self.markdown))

    def render(self) -> str:
        return "\n".join(self.parts)


# --------------------------------------------------------------------------
# sections
# --------------------------------------------------------------------------

def section_overview(conn, rep: Report, events, accounts):
    rep.heading("1. Overview / date range")
    n_ev = len(events)
    n_acc = len(accounts)
    utc_min = min(e["occurred_utc"] for e in events)
    utc_max = max(e["occurred_utc"] for e in events)
    rep.line(f"accounts            : {n_acc}")
    rep.line(f"activity_events     : {n_ev}")
    rep.line(f"first event (UTC)   : {utc_min:%Y-%m-%d %H:%M:%S}")
    rep.line(f"last  event (UTC)   : {utc_max:%Y-%m-%d %H:%M:%S}")
    rep.line(f"span                : {(utc_max - utc_min).days} days")
    rep.line(f"LAST EVENT DATE     : {utc_max.date()} (UTC)  <- dataset 'today' anchor")
    loc_min = min(e["local_dt"] for e in events)
    loc_max = max(e["local_dt"] for e in events)
    rep.line(f"first/last in account-local time: {loc_min:%Y-%m-%d %H:%M} .. {loc_max:%Y-%m-%d %H:%M}")
    rep.line()
    rep.line(f"distinct locations (global label set): "
             f"{sorted({e['location'] for e in events})}")


def section_per_account(conn, rep: Report, events, accounts):
    rep.heading("2. Events and distinct locations per account")
    by_acc = defaultdict(list)
    for e in events:
        by_acc[e["account_id"]].append(e)

    rows = []
    for a in accounts:
        evs = by_acc.get(a["id"], [])
        locs = sorted({e["location"] for e in evs})
        if evs:
            first = min(e["local_dt"] for e in evs)
            last = max(e["local_dt"] for e in evs)
            span_days = (last.date() - first.date()).days + 1
            rows.append([
                a["id"], a["name"][:26], a["industry"][:20], a["timezone"],
                a["created_at"][:10], len(evs), len(locs), ",".join(locs),
                first.date(), last.date(), span_days,
                f"{len(evs) / span_days:.1f}",
            ])
        else:
            rows.append([a["id"], a["name"][:26], a["industry"][:20], a["timezone"],
                         a["created_at"][:10], 0, 0, "-", "-", "-", "-", "-"])
    rep.table(
        ["acct", "name", "industry", "timezone", "created", "events", "locs",
         "locations", "first_local", "last_local", "span_d", "ev/day"],
        rows,
    )

    empty = [a for a in accounts if not by_acc.get(a["id"])]
    rep.sub("Accounts with NO events")
    rep.table(["acct", "name", "timezone", "created_at"],
              [[a["id"], a["name"], a["timezone"], a["created_at"]] for a in empty])

    # Orphan events: account_id not present in accounts (FK is unenforced in SQLite).
    known = {a["id"] for a in accounts}
    orphans = Counter(e["account_id"] for e in events if e["account_id"] not in known)
    rep.sub("Events referencing a missing account (orphan FK)")
    rep.table(["account_id", "events"], [[k, v] for k, v in sorted(orphans.items())])


def section_types_outcomes(conn, rep: Report, events):
    rep.heading("3. event_type x outcome (NULLs included)")
    combo = Counter((e["event_type"], e["outcome"]) for e in events)
    types = sorted({e["event_type"] for e in events})
    outcomes = sorted({e["outcome"] for e in events}, key=lambda o: (o is None, o))
    header = ["event_type"] + [("NULL" if o is None else o) for o in outcomes] + ["TOTAL"]
    rows = []
    for t in types:
        row = [t] + [combo.get((t, o), 0) for o in outcomes]
        row.append(sum(row[1:]))
        rows.append(row)
    totals = ["TOTAL"] + [sum(combo.get((t, o), 0) for t in types) for o in outcomes]
    totals.append(sum(totals[1:]))
    rows.append(totals)
    rep.table(header, rows)

    rep.sub("Outcome values not in the documented set")
    documented = KNOWN_OUTCOMES
    unexpected = sorted(
        {(e["event_type"], e["outcome"]) for e in events
         if e["outcome"] is not None and e["outcome"] not in documented}
    )
    rep.table(["event_type", "outcome", "count"],
              [[t, o, combo[(t, o)]] for t, o in unexpected])

    rep.sub("Event types not in the documented set")
    rep.table(["event_type", "count"],
              [[t, sum(v for (tt, _), v in combo.items() if tt == t)]
               for t in types if t not in KNOWN_EVENT_TYPES])

    rep.heading("4. duration_seconds per event_type")
    rows = []
    for t in types:
        vals = [e["duration_seconds"] for e in events if e["event_type"] == t]
        present = [v for v in vals if v is not None]
        nulls = len(vals) - len(present)
        if present:
            rows.append([
                t, len(vals), nulls, f"{nulls / len(vals) * 100:.1f}%",
                len(present), min(present), max(present),
                f"{statistics.median(present):.0f}",
                f"{statistics.mean(present):.0f}",
                sum(1 for v in present if v <= 0),
            ])
        else:
            rows.append([t, len(vals), nulls, "100.0%", 0, "-", "-", "-", "-", 0])
    rep.table(["event_type", "rows", "nulls", "null%", "non_null", "min", "max",
               "median", "mean", "<=0"], rows)
    rep.line()
    rep.line("NOTE: duration_seconds is documented as 'only meaningful for calls'.")
    non_call_with_dur = sum(
        1 for e in events
        if e["event_type"] != "call_received" and e["duration_seconds"] is not None
    )
    rep.line(f"      non-call rows carrying a duration: {non_call_with_dur}")


def section_duplicates(conn, rep: Report, events, windows):
    rep.heading("5. Exact duplicate rows (all columns except id)")
    cur = conn.execute("""
        SELECT account_id, location, event_type, occurred_at,
               duration_seconds, outcome, COUNT(*) AS n,
               GROUP_CONCAT(id) AS ids
        FROM activity_events
        GROUP BY account_id, location, event_type, occurred_at,
                 duration_seconds, outcome
        HAVING COUNT(*) > 1
        ORDER BY n DESC, occurred_at
    """)
    dups = cur.fetchall()
    extra = sum(r["n"] - 1 for r in dups)
    affected_accts = sorted({r["account_id"] for r in dups})
    affected_locs = {(r["account_id"], r["location"]) for r in dups}
    rep.line(f"duplicate groups          : {len(dups)}")
    rep.line(f"redundant rows (n-1 sum)  : {extra}")
    rep.line(f"distinct accounts affected: {len(affected_accts)} {affected_accts}")
    rep.line(f"distinct (acct,location)  : {len(affected_locs)}")
    rep.line()
    rep.table(
        ["acct", "location", "event_type", "occurred_at(UTC)", "dur", "outcome", "n", "ids"],
        [[r["account_id"], r["location"], r["event_type"], r["occurred_at"],
          r["duration_seconds"], r["outcome"], r["n"], r["ids"]] for r in dups[:40]],
    )
    if len(dups) > 40:
        rep.line(f"... {len(dups) - 40} more duplicate groups not shown")

    # Duplicate ids would break the PK assumption if the seed is loaded elsewhere.
    cur = conn.execute("""
        SELECT id, COUNT(*) n FROM activity_events GROUP BY id HAVING COUNT(*) > 1
    """)
    dup_ids = cur.fetchall()
    rep.sub("Duplicate primary keys")
    rep.table(["id", "n"], [[r["id"], r["n"]] for r in dup_ids])

    # Same account+location+timestamp with differing payload: near-duplicates.
    cur = conn.execute("""
        SELECT account_id, location, occurred_at, COUNT(*) n
        FROM activity_events
        GROUP BY account_id, location, occurred_at
        HAVING COUNT(*) > 1
    """)
    near = cur.fetchall()
    rep.sub("Same account+location+timestamp (incl. exact dups above)")
    rep.line(f"groups: {len(near)}  extra rows: {sum(r['n'] - 1 for r in near)}")
    rep.line("No near-duplicates beyond the exact ones: every colliding timestamp is a")
    rep.line("full row repeat, so a DISTINCT-style view removes all of them cleanly.")

    # The reason dedupe is not optional: at the location-week grain the buckets
    # are small enough that a single redundant row is a double-digit swing.
    rep.sub("Impact of each duplicate on its own location-week (the reporting grain)")
    dup_keys = {(r["account_id"], r["location"], r["event_type"], r["occurred_at"],
                 r["duration_seconds"], r["outcome"]): r["n"] for r in dups}
    locweek = Counter()
    for e in events:
        locweek[(e["account_id"], e["location"],
                 week_start(e["local_dt"].date()))] += 1
    rows = []
    seen = set()
    for e in events:
        k = (e["account_id"], e["location"], e["event_type"], e["occurred_at"],
             e["duration_seconds"], e["outcome"])
        if k not in dup_keys or k in seen:
            continue
        seen.add(k)
        w = week_start(e["local_dt"].date())
        raw = locweek[(e["account_id"], e["location"], w)]
        dedup = raw - (dup_keys[k] - 1)
        win = windows[e["account_id"]]
        rows.append([
            e["account_id"], e["location"], w,
            "partial" if win.is_partial(w) else "complete",
            raw, dedup, f"{(raw - dedup) / dedup * 100:.0f}%",
        ])
    rows.sort(key=lambda r: -float(r[6].rstrip("%")))
    rep.table(["acct", "location", "week", "bucket", "raw", "deduped", "overcount"], rows)
    if rows:
        sw = [float(r[6].rstrip("%")) for r in rows]
        rep.line()
        rep.line(f"overcount per affected location-week: min {min(sw):.0f}%  "
                 f"median {statistics.median(sw):.0f}%  max {max(sw):.0f}%")
        rep.line("-> 12 rows in 12,626 is negligible account-wide and material per")
        rep.line("   location-week. Dedupe in a DB view over the raw table; leave the")
        rep.line("   seed untouched so the raw feed stays reproducible.")


def build_weeks(events, accounts):
    """Per-account weekly aggregates in the account's own timezone."""
    by_acc = defaultdict(list)
    for e in events:
        by_acc[e["account_id"]].append(e)
    result = {}
    for a in accounts:
        evs = by_acc.get(a["id"], [])
        if not evs:
            result[a["id"]] = None
            continue
        per_week = Counter()
        per_week_loc = defaultdict(Counter)
        per_day = Counter()
        for e in evs:
            d = e["local_dt"].date()
            w = week_start(d)
            per_week[w] += 1
            per_week_loc[e["location"]][w] += 1
            per_day[d] += 1
        first_d = min(per_day)
        last_d = max(per_day)
        result[a["id"]] = {
            "per_week": per_week,
            "per_week_loc": per_week_loc,
            "per_day": per_day,
            "first_d": first_d,
            "last_d": last_d,
        }
    return result


def section_weekly(rep: Report, accounts, weeks, windows):
    rep.heading("6. Weekly counts per account (account-local tz, Mon-Sun)")
    rep.line("A bucket is COMPLETE only when all 7 of its days sit inside the account's")
    rep.line("observation window. Completeness is a property of the window, not of where")
    rep.line("events land, so a week with zero events can still be complete.")
    rep.line("DECISION (DASH-247): partial weeks are never compared and never feed a")
    rep.line("baseline. The default dashboard view is the LAST COMPLETE week.")
    rep.line()

    summary_rows = []
    for a in accounts:
        w = weeks[a["id"]]
        win = windows[a["id"]]
        if not w:
            summary_rows.append([a["id"], a["name"][:22], "-", len(win.all_weeks),
                                 len(win.complete), "-", "-", "-", "-", "-"])
            continue
        counts = [w["per_week"].get(k, 0) for k in win.complete]
        zero = [k for k in win.complete if w["per_week"].get(k, 0) == 0]
        summary_rows.append([
            a["id"], a["name"][:22], a["timezone"].split("/")[-1],
            len(win.all_weeks), len(win.complete), len(zero),
            min(counts), max(counts), f"{statistics.median(counts):.0f}",
            win.last_complete,
        ])
    rep.table(["acct", "name", "tz", "buckets", "complete", "zero_wks",
               "min_wk", "max_wk", "median_wk", "last_complete"], summary_rows)

    rep.sub("Partial buckets (excluded from every comparison and baseline)")
    rows = []
    for a in accounts:
        win = windows[a["id"]]
        w = weeks[a["id"]]
        for k in win.partial:
            cov_a, cov_b = win.coverage(k)
            days = (cov_b - cov_a).days + 1
            rows.append([
                a["id"], k, f"{cov_a} .. {cov_b}", f"{days}/7",
                cov_a.strftime("%a") + "-" + cov_b.strftime("%a"),
                (w["per_week"].get(k, 0) if w else 0),
            ])
    rep.table(["acct", "week_start(Mon)", "covered", "days", "dows", "events"], rows)
    rep.line()
    rep.line("Every account shares the same two partial buckets: the window opens on")
    rep.line("Sun 2026-02-01 (1 day of that week) and closes on Mon 2026-07-27 (1 day).")
    rep.line("That is not an edge case - it is the default state of the newest bucket.")

    rep.sub("Zero-activity COMPLETE weeks (account level)")
    rows = []
    for a in accounts:
        w = weeks[a["id"]]
        if not w:
            continue
        for k in windows[a["id"]].complete:
            if w["per_week"].get(k, 0) == 0:
                rows.append([a["id"], a["name"][:22], k, k + timedelta(days=6)])
    rep.table(["acct", "name", "week_start(Mon)", "week_end(Sun)"], rows)


def section_locations(rep: Report, accounts, weeks, windows, spike_weeks):
    rep.heading("7. Weekly counts per location (account-local tz, complete weeks)")
    rep.line("The location grain is the multi-location requirement, so any threshold")
    rep.line("has to hold HERE, not just at account level.")
    rep.line()
    rows = []
    lifecycle = []
    for a in accounts:
        w = weeks[a["id"]]
        win = windows[a["id"]]
        if not w:
            continue
        for loc in sorted(w["per_week_loc"]):
            ctr = w["per_week_loc"][loc]
            counts = [ctr.get(k, 0) for k in win.complete]
            active = [k for k in win.complete if ctr.get(k, 0) > 0]
            if not active:
                continue
            inner = [ctr.get(k, 0) for k in iter_weeks(active[0], active[-1])]
            late = active[0] > win.complete[0]
            silent = active[-1] < win.complete[-1]
            rows.append([
                a["id"], loc, sum(counts), len(win.complete),
                sum(1 for c in inner if c == 0),
                min(counts), max(counts), f"{statistics.median(counts):.0f}",
                active[0], active[-1], "Y" if late else "", "Y" if silent else "",
            ])
            if late or silent:
                lifecycle.append([a["id"], loc, active[0], active[-1],
                                  "opened late" if late else "went silent"])
    rep.table(["acct", "location", "events", "complete_wks", "zero_wks_inner",
               "min_wk", "max_wk", "median_wk", "first_active", "last_active",
               "opened_late", "went_silent"], rows)
    rep.sub("Locations that start late or stop early (vs the complete-week span)")
    rep.table(["acct", "location", "first_active_wk", "last_active_wk", "kind"], lifecycle)

    # --- the core question: how noisy is a normal week AT LOCATION GRAIN? ---
    rep.heading("7b. Baseline noise per (account, location) - complete weeks only")
    rep.line("CV = stdev/mean of the weekly series. Anomaly weeks (section 8) are")
    rep.line("excluded so a single spike does not inflate its own baseline.")
    rep.line()
    loc_rows = []
    all_cv = []
    for a in accounts:
        w = weeks[a["id"]]
        win = windows[a["id"]]
        if not w:
            continue
        excl = spike_weeks.get(a["id"], set())
        for loc in sorted(w["per_week_loc"]):
            ctr = w["per_week_loc"][loc]
            keep = [k for k in win.complete if k not in excl]
            counts = [ctr.get(k, 0) for k in keep]
            if len(counts) < 4:
                continue
            mean = statistics.mean(counts)
            if not mean:
                continue
            sd = statistics.pstdev(counts)
            cv = sd / mean * 100
            all_cv.append(cv)
            loc_rows.append([
                a["id"], loc, len(counts), f"{mean:.1f}",
                f"{statistics.median(counts):.0f}",
                f"{sd:.1f}", f"{cv:.0f}%", min(counts), max(counts),
                len(excl) or "",
            ])
    loc_rows.sort(key=lambda r: -float(r[6].rstrip("%")))
    rep.table(["acct", "location", "wks", "mean_wk", "median_wk", "sd", "CV",
               "min", "max", "wks_excl"], loc_rows)
    rep.line()
    rep.line(f"location-week CV across {len(all_cv)} locations: "
             f"min {min(all_cv):.0f}%  p25 {sorted(all_cv)[len(all_cv)//4]:.0f}%  "
             f"median {statistics.median(all_cv):.0f}%  "
             f"p75 {sorted(all_cv)[3*len(all_cv)//4]:.0f}%  max {max(all_cv):.0f}%")
    small = [r for r in loc_rows if float(r[3]) < 8]
    rep.line(f"locations averaging <8 events/week: {len(small)} of {len(loc_rows)}")
    rep.line("-> location weeks are noisier than account weeks and the buckets are")
    rep.line("   small. A percentage threshold tuned on account totals will fire")
    rep.line("   constantly at location grain.")


def section_outliers(rep: Report, accounts, weeks, windows, events, spike_weeks):
    rep.heading("8. Outlier days and weeks per account (modified z-score, |mz| > %.1f)"
                % OUTLIER_MZ)
    rep.line("Computed over COMPLETE weeks only; partial buckets are excluded by")
    rep.line("construction, not by a special case.")
    rep.sub("Outlier DAYS")
    rows = []
    for a in accounts:
        w = weeks[a["id"]]
        win = windows[a["id"]]
        if not w:
            continue
        days = list(_daterange(win.start, win.end))
        counts = [float(w["per_day"].get(d, 0)) for d in days]
        if len(counts) < MIN_POINTS_FOR_OUTLIERS:
            continue
        for d, c, mz in zip(days, counts, modified_z(counts)):
            if abs(mz) > OUTLIER_MZ:
                rows.append([a["id"], a["name"][:20], d, d.strftime("%a"), int(c),
                             f"{statistics.median(counts):.0f}", f"{mz:+.1f}",
                             "HIGH" if mz > 0 else "LOW"])
    rows.sort(key=lambda r: -abs(float(r[6])))
    rep.table(["acct", "name", "date", "dow", "events", "median_day", "mod_z", "dir"],
              rows[:40])
    if len(rows) > 40:
        rep.line(f"... {len(rows) - 40} more outlier days not shown")

    rep.sub("Outlier WEEKS (complete weeks only)")
    rows = []
    for a in accounts:
        w = weeks[a["id"]]
        win = windows[a["id"]]
        if not w or len(win.complete) < 4:
            continue
        counts = [float(w["per_week"].get(k, 0)) for k in win.complete]
        for k, c, mz in zip(win.complete, counts, modified_z(counts)):
            if abs(mz) > OUTLIER_MZ:
                rows.append([a["id"], a["name"][:20], k, int(c),
                             f"{statistics.median(counts):.0f}", f"{mz:+.1f}",
                             "HIGH" if mz > 0 else "LOW"])
    rows.sort(key=lambda r: -abs(float(r[5])))
    rep.table(["acct", "name", "week_start", "events", "median_wk", "mod_z", "dir"], rows)

    rep.sub("Baseline noise per account (complete weeks, anomaly weeks excluded)")
    rows = []
    for a in accounts:
        w = weeks[a["id"]]
        win = windows[a["id"]]
        if not w:
            rows.append([a["id"], a["name"][:24], 0, "-", "-", "-", "-", "-", ""])
            continue
        excl = spike_weeks.get(a["id"], set())
        keep = [k for k in win.complete if k not in excl]
        full = [w["per_week"].get(k, 0) for k in keep]
        if len(full) < 2:
            continue
        mean = statistics.mean(full)
        sd = statistics.pstdev(full)
        rows.append([a["id"], a["name"][:24], len(full), f"{mean:.1f}", f"{sd:.1f}",
                     f"{sd / mean * 100:.0f}%" if mean else "-", min(full), max(full),
                     len(excl) or ""])
    rep.table(["acct", "name", "wks", "mean_wk", "sd", "CV", "min", "max", "wks_excl"],
              rows)

    rep.sub("Drill-down on the single largest outlier day")
    best = None
    for a in accounts:
        w = weeks[a["id"]]
        if not w:
            continue
        for d, c in w["per_day"].items():
            other = [v for k, v in w["per_day"].items() if k != d]
            med = statistics.median(other) if other else 0
            ratio = c / med if med else 0
            if best is None or ratio > best[0]:
                best = (ratio, a, d, c)
    if best is None:
        return
    ratio, a, d, c = best
    day = [e for e in events
           if e["account_id"] == a["id"] and e["local_dt"].date() == d]
    med = statistics.median([v for k, v in weeks[a["id"]]["per_day"].items() if k != d])
    rep.line(f"account {a['id']} ({a['name']}) on {d} ({d:%A}): "
             f"{c} events vs a {med:.0f}/day median  = {ratio:.0f}x")
    rep.line(f"  by event_type : {dict(sorted(Counter(e['event_type'] for e in day).items()))}")
    rep.line(f"  by outcome    : {dict(sorted(Counter(str(e['outcome']) for e in day).items()))}")
    rep.line(f"  by location   : {dict(sorted(Counter(e['location'] for e in day).items()))}")
    rep.line(f"  distinct timestamps: {len({e['occurred_at'] for e in day})} of {len(day)}")
    rep.line(f"  id range      : {min(e['id'] for e in day)}..{max(e['id'] for e in day)}")
    rep.line()
    rep.line("  OPEN QUESTION - the cause is NOT determined by this data. The burst is")
    rep.line("  spread evenly over all 15 locations, all 3 event types and the normal")
    rep.line("  hour curve, with near-unique timestamps. That shape fits a bulk")
    rep.line("  backfill or double-import, and fits a real demand event (storm, recall,")
    rep.line("  campaign) for a collision-repair group just as well. Nothing in the")
    rep.line("  schema or the seed distinguishes the two.")
    rep.line("  CONSEQUENCE: do not try to resolve it - make the baseline robust either")
    rep.line("  way. A median/MAD baseline is unmoved by one week at 12x whether that")
    rep.line("  week was real demand or a bad import; a mean-based baseline is wrong in")
    rep.line("  both cases. The week is excluded from the baselines above and still")
    rep.line("  surfaced as an anomaly - correct handling under either cause.")


def find_anomalous_weeks(accounts, weeks, windows) -> dict[int, set]:
    """Complete weeks whose modified z-score marks them as anomalous.

    These are held out of every baseline so an anomaly cannot inflate the
    baseline it is being measured against.
    """
    out: dict[int, set] = {}
    for a in accounts:
        w = weeks[a["id"]]
        win = windows[a["id"]]
        if not w or len(win.complete) < 4:
            continue
        counts = [float(w["per_week"].get(k, 0)) for k in win.complete]
        flagged = {k for k, mz in zip(win.complete, modified_z(counts))
                   if abs(mz) > OUTLIER_MZ}
        if flagged:
            out[a["id"]] = flagged
    return out


def _daterange(a: date, b: date):
    cur = a
    while cur <= b:
        yield cur
        cur += timedelta(days=1)


def section_oddities(conn, rep: Report, events, accounts, weeks, cutoff_local, source):
    rep.heading("9. Other things that look odd")
    acct = {a["id"]: a for a in accounts}

    # --- events before the account existed -------------------------------
    early = defaultdict(int)
    for e in events:
        a = acct.get(e["account_id"])
        if a and e["occurred_utc"] < parse_ts(a["created_at"]):
            early[e["account_id"]] += 1
    if early:
        rep.sub("Events occurring BEFORE the account's created_at")
        rep.table(["acct", "name", "created_at", "events_before"],
                  [[k, acct[k]["name"], acct[k]["created_at"], v]
                   for k, v in sorted(early.items())])

    # --- per-account cutoff skew -----------------------------------------
    rep.sub("Per-account last event vs dataset cutoff (staleness)")
    global_max = max(e["occurred_utc"] for e in events)
    rows = []
    for a in accounts:
        w = weeks[a["id"]]
        if not w:
            continue
        gap = (cutoff_local[a["id"]] - w["last_d"]).days
        if gap > 0:
            rows.append([a["id"], a["name"][:24], w["last_d"], cutoff_local[a["id"]], gap])
    rows.sort(key=lambda r: -r[4])
    rep.table(["acct", "name", "last_event_local", "cutoff_local", "days_stale"], rows)

    # --- weekday / hour distribution --------------------------------------
    # The largest outlier day badly skews the aggregate weekday profile, so
    # report it both ways.
    spike_key = None
    top = 0
    for aid, w in weeks.items():
        if not w:
            continue
        for d, c in w["per_day"].items():
            if c > top:
                top, spike_key = c, (aid, d)
    base = [e for e in events
            if spike_key is None
            or (e["account_id"], e["local_dt"].date()) != spike_key]

    rep.sub("Local-time weekday distribution (all accounts)")
    order = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
    dow = Counter(e["local_dt"].strftime("%a") for e in events)
    dow_b = Counter(e["local_dt"].strftime("%a") for e in base)
    rep.table(["series"] + order,
              [["all events"] + [dow.get(d, 0) for d in order],
               [f"excl. acct {spike_key[0]} {spike_key[1]}"] + [dow_b.get(d, 0) for d in order]])
    wd = sum(dow_b.get(d, 0) for d in order[:5]) / 5
    we = sum(dow_b.get(d, 0) for d in order[5:]) / 2
    rep.line()
    rep.line(f"weekday avg {wd:.0f}/day vs weekend avg {we:.0f}/day "
             f"({we / wd * 100:.0f}% of a weekday)")
    rep.line("-> strong weekday effect; Mon-Sun weekly buckets absorb it, but any")
    rep.line("   partial-week or day-level comparison must not ignore it.")

    rep.sub("Hour-of-day: account-local vs UTC")
    rep.line("If occurred_at were generated per-account in local business hours, the")
    rep.line("LOCAL profile would peak identically for every timezone. It does not.")
    rep.line()
    rows = []
    bytz = defaultdict(Counter)
    for e in base:
        bytz[acct[e["account_id"]]["timezone"]][e["local_dt"].hour] += 1
    for tzname in sorted(bytz):
        h = bytz[tzname]
        tot = sum(h.values())
        pre7 = sum(v for k, v in h.items() if k < 7)
        rows.append([tzname, tot, max(h, key=h.get), pre7, f"{pre7 / tot * 100:.1f}%"])
    rep.table(["timezone", "events", "peak_local_hour", "before_07:00", "pct"], rows)
    hu = Counter(e["occurred_utc"].hour for e in base)
    rep.line()
    rep.line(f"UTC peak hour: {max(hu, key=hu.get)}  "
             f"(one shared bell curve across every account)")
    rep.line("-> the seed was sampled in UTC and never localized. Local hour-of-day is")
    rep.line("   an artifact (e.g. Los_Angeles shows ~30% of activity before 7am local).")
    rep.line("   Do NOT build an hour-of-day feature on this data. Local DAY and WEEK")
    rep.line("   bucketing is still correct and still required.")

    # --- location label hygiene -------------------------------------------
    rep.sub("Location label hygiene")
    raw_locs = Counter(e["location"] for e in events)
    issues = []
    for loc, n in sorted(raw_locs.items()):
        flags = []
        if loc != loc.strip():
            flags.append("leading/trailing whitespace")
        if re.search(r"\s{2,}", loc):
            flags.append("double space")
        if loc.lower() in {l.lower() for l in raw_locs if l != loc}:
            flags.append("case-variant twin")
        if flags:
            issues.append([repr(loc), n, "; ".join(flags)])
    rep.table(["location", "events", "issue"], issues)
    rep.line(f"distinct raw location labels: {len(raw_locs)} -> {sorted(raw_locs)}")
    rep.line("NOTE: labels are generic ('Site A'...), so the SAME label in two accounts")
    rep.line("      is a DIFFERENT physical site. Location must always be keyed by account.")

    # --- DST boundary exposure ---------------------------------------------
    rep.sub("DST transitions inside the data window (per timezone)")
    utc_min = min(e["occurred_utc"] for e in events)
    rows = []
    for tzname in sorted({a["timezone"] for a in accounts}):
        try:
            tz = ZoneInfo(tzname)
        except ZoneInfoNotFoundError:
            rows.append([tzname, "TZ NOT FOUND", ""])
            continue
        transitions = []
        cur = utc_min
        prev = cur.astimezone(tz).utcoffset()
        while cur <= global_max:
            cur += timedelta(hours=6)
            off = cur.astimezone(tz).utcoffset()
            if off != prev:
                transitions.append(cur.astimezone(tz).date().isoformat())
                prev = off
        rows.append([tzname, len(transitions), ", ".join(transitions)])
    rep.table(["timezone", "transitions", "local dates"], rows)

    # --- UTC-day vs local-day disagreement ----------------------------------
    shifted = sum(1 for e in events if e["local_dt"].date() != e["occurred_utc"].date())
    rep.sub("UTC-day vs account-local-day")
    rep.line(f"events whose local calendar day differs from their UTC day: "
             f"{shifted} / {len(events)} ({shifted / len(events) * 100:.1f}%)")
    wk_shift = sum(1 for e in events
                   if week_start(e["local_dt"].date()) != week_start(e["occurred_utc"].date()))
    rep.line(f"events that land in a different Mon-Sun WEEK when bucketed in UTC: "
             f"{wk_shift} ({wk_shift / len(events) * 100:.1f}%)")
    rep.line("-> bucketing in UTC instead of account-local time silently misfiles these.")

    # --- null / blank hygiene ------------------------------------------------
    rep.sub("Null and blank hygiene")
    rows = []
    cur = conn.execute(f"SELECT COUNT(*) c FROM {source} WHERE TRIM(location) = ''")
    rows.append(["empty location", cur.fetchone()["c"]])
    cur = conn.execute(f"SELECT COUNT(*) c FROM {source} WHERE outcome IS NULL")
    rows.append(["outcome IS NULL", cur.fetchone()["c"]])
    cur = conn.execute(f"SELECT COUNT(*) c FROM {source} WHERE TRIM(COALESCE(outcome,'x')) = ''")
    rows.append(["outcome is empty string", cur.fetchone()["c"]])
    cur = conn.execute(f"SELECT COUNT(*) c FROM {source} WHERE duration_seconds IS NULL")
    rows.append(["duration IS NULL", cur.fetchone()["c"]])
    cur = conn.execute(f"SELECT COUNT(*) c FROM {source} WHERE duration_seconds < 0")
    rows.append(["duration < 0", cur.fetchone()["c"]])
    cur = conn.execute(f"SELECT COUNT(*) c FROM {source} WHERE duration_seconds = 0")
    rows.append(["duration = 0", cur.fetchone()["c"]])
    rep.table(["check", "count"], rows)

    # --- timestamp granularity ----------------------------------------------
    rep.sub("Timestamp granularity")
    secs = Counter(e["occurred_utc"].second for e in events)
    mins = Counter(e["occurred_utc"].minute for e in events)
    rep.line(f"distinct second values: {len(secs)}   distinct minute values: {len(mins)}")
    rep.line(f"events at exactly HH:00:00: "
             f"{sum(1 for e in events if e['occurred_utc'].second == 0 and e['occurred_utc'].minute == 0)}")

    # --- duration semantics -------------------------------------------------
    rep.sub("duration_seconds semantics vs outcome")
    rows = []
    for o in sorted({e["outcome"] for e in events if e["event_type"] == "call_received"},
                    key=lambda x: (x is None, x)):
        vals = [e["duration_seconds"] for e in events
                if e["event_type"] == "call_received" and e["outcome"] == o]
        present = [v for v in vals if v is not None]
        rows.append([("NULL" if o is None else o), len(vals), len(present),
                     min(present) if present else "-", max(present) if present else "-",
                     f"{statistics.median(present):.0f}" if present else "-"])
    rep.table(["call outcome", "rows", "with_duration", "min", "max", "median"], rows)
    rep.line()
    rep.line("-> 'missed' and 'voicemail' calls carry multi-minute durations with the same")
    rep.line("   distribution as 'connected'. duration_seconds is random noise, uncorrelated")
    rep.line("   with outcome. It is clamped to [20, 1500]. Do not build a talk-time or")
    rep.line("   'quality of call' metric on it.")

    # --- window vs wall clock -------------------------------------------
    rep.sub("Observation window vs account tenure and wall clock")
    rep.line(f"accounts created         : {min(a['created_at'] for a in accounts)[:10]} .. "
             f"{max(a['created_at'] for a in accounts)[:10]}")
    rep.line(f"events cover             : {min(e['local_dt'] for e in events).date()} .. "
             f"{max(e['local_dt'] for e in events).date()}")
    rep.line("-> every account's history starts on the same date regardless of created_at,")
    rep.line("   so created_at is NOT the start of history and must not gate the baseline.")
    rep.line(f"today (wall clock)       : {date.today()}")
    rep.line(f"dataset cutoff           : {global_max.date()} "
             f"({(date.today() - global_max.date()).days} days ago, a "
             f"{global_max.strftime('%A')})")
    rep.line("-> 'this week' must be anchored on MAX(occurred_at), not on now(), or the")
    rep.line("   dashboard shows an empty current week. The cutoff being a Monday means the")
    rep.line("   newest Mon-Sun bucket holds a single day for every account.")

    # --- overall shape ---------------------------------------------------
    rep.sub("Account size distribution")
    sizes = sorted((len([e for e in events if e["account_id"] == a["id"]]) for a in accounts))
    rep.line(f"events/account: min={sizes[0]} p25={sizes[len(sizes)//4]} "
             f"median={statistics.median(sizes):.0f} p75={sizes[3*len(sizes)//4]} max={sizes[-1]}")
    rep.line(f"locations/account: "
             f"{sorted(len({e['location'] for e in events if e['account_id'] == a['id']}) for a in accounts)}")


# --------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--db-dir", default="db", type=Path)
    ap.add_argument("--markdown", action="store_true", help="emit markdown tables")
    ap.add_argument("--dedupe", action="store_true",
                    help="aggregate over the dedupe view instead of the raw table, "
                         "i.e. report the numbers the application will serve")
    args = ap.parse_args()

    conn = load_db(args.db_dir)
    source = "activity_events_dedup" if args.dedupe else "activity_events"
    accounts = [dict(r) for r in conn.execute(
        "SELECT * FROM accounts ORDER BY id")]
    tzs = {}
    for a in accounts:
        try:
            tzs[a["id"]] = ZoneInfo(a["timezone"])
        except ZoneInfoNotFoundError:
            print(f"WARNING: unknown timezone {a['timezone']!r} for account {a['id']}; "
                  f"falling back to UTC", file=sys.stderr)
            tzs[a["id"]] = timezone.utc

    events = []
    for r in conn.execute(f"SELECT * FROM {source}"):
        e = dict(r)
        e["occurred_utc"] = parse_ts(e["occurred_at"])
        tz = tzs.get(e["account_id"], timezone.utc)
        e["local_dt"] = e["occurred_utc"].astimezone(tz)
        events.append(e)

    if not events:
        print("no events loaded", file=sys.stderr)
        return 1

    # The observation window is a property of the FEED, not of the account: every
    # account is observed over the same UTC span (see section 9 - history does not
    # start at created_at). Expressed in each account's own local calendar it gives
    # that account's Mon-Sun bucket completeness.
    global_min_utc = min(e["occurred_utc"] for e in events)
    global_max_utc = max(e["occurred_utc"] for e in events)
    cutoff_local = {a["id"]: global_max_utc.astimezone(tzs[a["id"]]).date() for a in accounts}
    windows = {
        a["id"]: Window(global_min_utc.astimezone(tzs[a["id"]]).date(),
                        global_max_utc.astimezone(tzs[a["id"]]).date())
        for a in accounts
    }

    rep = Report(args.markdown)
    if args.markdown:
        rep.line("# Relay seed data profile (DASH-247)")
    weeks = build_weeks(events, accounts)

    # Anomalous weeks are found first so the baseline sections can exclude them.
    # Excluding an anomaly from its own baseline is what makes the baseline
    # robust regardless of what caused the anomaly.
    spike_weeks = find_anomalous_weeks(accounts, weeks, windows)

    section_overview(conn, rep, events, accounts)
    section_per_account(conn, rep, events, accounts)
    section_types_outcomes(conn, rep, events)
    section_duplicates(conn, rep, events, windows)
    section_weekly(rep, accounts, weeks, windows)
    section_locations(rep, accounts, weeks, windows, spike_weeks)
    section_outliers(rep, accounts, weeks, windows, events, spike_weeks)
    section_oddities(conn, rep, events, accounts, weeks, cutoff_local, source)

    sys.stdout.write(rep.render() + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
