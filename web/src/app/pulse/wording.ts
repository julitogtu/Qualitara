import { PulseRow, TypeSplit } from './pulse-api';

// Plain-language rendering of the server's verdicts. No statistics happen here: the verdict, band
// and tail probabilities all come from the API.

const VERDICT_LABELS: Record<PulseRow['verdict'], string> = {
  normal: 'Normal',
  above: 'Unusually high',
  below: 'Unusually low',
  not_enough_volume: 'Not enough volume to judge',
  insufficient_history: 'Not enough history yet',
};

export function verdictLabel(row: PulseRow): string {
  return VERDICT_LABELS[row.verdict];
}

// "Less than 1 in N" must stay true: pick the largest N with p < 1/N. The flag thresholds guarantee
// at least 1 in 40 (below, p < 0.025) and 1 in 200 (above, p < 0.005). Capped at 1,000.
const ODDS = [1000, 500, 200, 100, 40];

function oneIn(p: number): number | null {
  return ODDS.find((n) => p < 1 / n) ?? null;
}

/** One sentence for a flagged row; null for anything else. */
export function verdictExplanation(row: PulseRow): string | null {
  const p = row.verdict === 'above' ? row.pHigh : row.verdict === 'below' ? row.pLow : null;
  if (p === null) {
    return null;
  }
  const n = oneIn(p);
  const odds = n === null ? 'rarely' : `less than 1 in ${n.toLocaleString('en-US')} weeks`;
  return row.verdict === 'above'
    ? `Unusually high: a week this busy happens by chance ${odds}.`
    : `Unusually low: a week this quiet happens by chance ${odds}.`;
}

export function bandLabel(row: PulseRow): string {
  return row.band ? `${row.band.lo}–${row.band.hi}` : '—';
}

const TYPE_LABELS: Record<string, string> = {
  call_received: 'calls',
  lead_created: 'leads',
  appointment_set: 'appointments',
};
const TYPE_ORDER = Object.keys(TYPE_LABELS);

/** "calls 34 (connected 18, missed 10, unspecified 1)", in calls/leads/appointments order. */
export function splitLabels(byType: TypeSplit[]): string[] {
  const rank = (t: string) => (TYPE_ORDER.includes(t) ? TYPE_ORDER.indexOf(t) : TYPE_ORDER.length);
  return [...byType]
    .sort((a, b) => rank(a.eventType) - rank(b.eventType))
    .map((t) => {
      const outcomes = Object.entries(t.outcomes)
        .sort(([a], [b]) => (a === 'unspecified' ? 1 : b === 'unspecified' ? -1 : a.localeCompare(b)))
        .map(([outcome, n]) => `${outcome.replace('_', '-')} ${n}`)
        .join(', ');
      return `${TYPE_LABELS[t.eventType] ?? t.eventType} ${t.count}${outcomes ? ` (${outcomes})` : ''}`;
    });
}

// Dates on the wire are calendar dates in the account's timezone. Format them as plain dates (UTC
// midnight, formatted in UTC) so the viewer's own timezone can't shift the day.
function asDate(isoDate: string): Date {
  const [y, m, d] = isoDate.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d));
}

const DAY = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  weekday: 'short',
  month: 'short',
  day: 'numeric',
});
const YEAR = new Intl.DateTimeFormat('en-US', { timeZone: 'UTC', year: 'numeric' });

/** "Mon Jul 20 – Sun Jul 26, 2026 · America/Chicago" */
export function periodLabel(weekStart: string, weekEnd: string, timezone: string): string {
  const start = asDate(weekStart);
  const end = asDate(weekEnd);
  const day = (d: Date) => DAY.format(d).replace(',', '');
  const startYear = YEAR.format(start) === YEAR.format(end) ? '' : `, ${YEAR.format(start)}`;
  return `${day(start)}${startYear} – ${day(end)}, ${YEAR.format(end)} · ${timezone}`;
}
