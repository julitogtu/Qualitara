import { HttpErrorResponse, httpResource } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { Router } from '@angular/router';

import { AccountSummary, accountsUrl, Pulse, pulseUrl } from './pulse-api';
import { bandLabel, periodLabel, splitLabels, verdictExplanation, verdictLabel } from './wording';

/**
 * The whole screen. State is the route: `id` and `week` are route params bound as signal inputs,
 * the pulse request is derived from them, and every control only navigates.
 */
@Component({
  selector: 'app-pulse-view',
  templateUrl: './pulse-view.html',
})
export class PulseView {
  private readonly router = inject(Router);

  readonly id = input.required<string>();
  /** Absent only when /accounts/:id could not be resolved to a week (unknown account, no data). */
  readonly week = input<string>();

  protected readonly accounts = httpResource<AccountSummary[]>(() => accountsUrl());
  protected readonly pulse = httpResource<Pulse>(() => pulseUrl(this.id(), this.week()));

  /** HTTP status of a failed pulse request (0 when unreachable), null when it has not failed. */
  protected readonly errorStatus = computed(() => {
    const error = this.pulse.error();
    if (!error) {
      return null;
    }
    return error instanceof HttpErrorResponse ? error.status : 0;
  });

  protected readonly periodLabel = computed(() => {
    const p = this.pulse.hasValue() ? this.pulse.value() : undefined;
    return p?.period ? periodLabel(p.period.weekStart, p.period.weekEnd, p.timezone) : null;
  });

  protected readonly verdictLabel = verdictLabel;
  protected readonly verdictExplanation = verdictExplanation;
  protected readonly bandLabel = bandLabel;
  protected readonly splitLabels = splitLabels;

  /** /accounts/:id, which the route guard resolves to that account's default week. */
  protected selectAccount(id: string): void {
    this.router.navigate(['/accounts', id]);
  }

  protected goToWeek(week: string | null): void {
    if (week) {
      this.router.navigate(['/accounts', this.id(), 'week', week]);
    }
  }
}
