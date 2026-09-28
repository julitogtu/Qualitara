import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

// Wire shapes of GET /api/accounts and GET /api/accounts/{id}/pulse. Dates are YYYY-MM-DD, account-local.

export interface AccountSummary {
  id: number;
  name: string;
  timezone: string;
}

export type Verdict = 'normal' | 'above' | 'below' | 'not_enough_volume' | 'insufficient_history';

export interface TypeSplit {
  eventType: string;
  count: number;
  /** A NULL outcome arrives keyed "unspecified". */
  outcomes: Record<string, number>;
}

export interface PulseRow {
  location: string | null;
  count: number;
  verdict: Verdict;
  lambda: number | null;
  band: { lo: number; hi: number } | null;
  pLow: number | null;
  pHigh: number | null;
  baselineWeeks: number;
  byType: TypeSplit[];
}

export interface PulsePeriod {
  weekStart: string;
  weekEnd: string;
  isDefault: boolean;
  firstCompleteWeek: string;
  lastCompleteWeek: string;
  previousWeek: string | null;
  nextWeek: string | null;
}

export interface Pulse {
  accountId: number;
  accountName: string;
  timezone: string;
  /** Null only when no data has been loaded at all. */
  period: PulsePeriod | null;
  state: 'ok' | 'empty';
  account: PulseRow;
  /** Ranked by the server; never re-sorted here. */
  locations: PulseRow[];
}

export function accountsUrl(): string {
  return '/api/accounts';
}

export function pulseUrl(accountId: string | number, week?: string): string {
  const base = `/api/accounts/${encodeURIComponent(accountId)}/pulse`;
  return week ? `${base}?week=${encodeURIComponent(week)}` : base;
}

@Injectable({ providedIn: 'root' })
export class PulseApi {
  private readonly http = inject(HttpClient);

  accounts(): Observable<AccountSummary[]> {
    return this.http.get<AccountSummary[]>(accountsUrl());
  }

  pulse(accountId: string | number, week?: string): Observable<Pulse> {
    return this.http.get<Pulse>(pulseUrl(accountId, week));
  }
}
