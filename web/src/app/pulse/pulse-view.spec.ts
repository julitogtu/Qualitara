import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { routes } from '../app.routes';
import { AccountSummary, Pulse } from './pulse-api';

// Wire-shaped fixtures. Values are illustrative; the numbers are asserted server-side (T3).
const ACCOUNTS: AccountSummary[] = [
  { id: 1, name: 'Summit Auto Group', timezone: 'America/Chicago' },
  { id: 6, name: 'Metro Collision Centers', timezone: 'America/New_York' },
];

function pulse(accountId: number, weekStart: string, previousWeek: string | null, nextWeek: string | null): Pulse {
  const account = ACCOUNTS.find((a) => a.id === accountId)!;
  return {
    accountId,
    accountName: account.name,
    timezone: account.timezone,
    period: {
      weekStart,
      weekEnd: weekStart, // not used by these tests
      isDefault: false,
      firstCompleteWeek: '2026-02-02',
      lastCompleteWeek: '2026-07-20',
      previousWeek,
      nextWeek,
    },
    state: 'ok',
    account: {
      location: null,
      count: 0,
      verdict: 'normal',
      lambda: null,
      band: null,
      pLow: null,
      pHigh: null,
      baselineWeeks: 12,
      byType: [],
    },
    locations: [],
  };
}

describe('PulseView routing', () => {
  let http: HttpTestingController;
  let harness: RouterTestingHarness;
  let router: Router;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes, withComponentInputBinding()),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    harness = await RouterTestingHarness.create();
  });

  afterEach(() => http.verify());

  // Not fixture.whenStable(): a pending httpResource request keeps the app unstable, which is
  // exactly the state these tests inspect. Let the router's async work run, then flush effects
  // (so httpResource issues its request for the current inputs) and change detection.
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve));
    TestBed.tick();
  }

  // Not harness.navigateByUrl(): it waits for stability too. Navigation is not awaited either, since
  // a guard may be waiting on a request the test has yet to flush.
  async function visit(url: string): Promise<void> {
    void router.navigateByUrl(url);
    await settle();
  }

  function flushAccounts(): void {
    http.match('/api/accounts').forEach((r) => r.flush(ACCOUNTS));
  }

  function el<T extends HTMLElement>(testId: string): T {
    return harness.routeNativeElement!.querySelector(`[data-testid="${testId}"]`) as T;
  }

  it('route params drive the API call (account + week)', async () => {
    await visit('/accounts/6/week/2026-06-01');
    flushAccounts();

    http.expectOne('/api/accounts/6/pulse?week=2026-06-01').flush(pulse(6, '2026-06-01', '2026-05-25', '2026-06-08'));
    await settle();
    expect(el('account-name').textContent).toContain('Metro Collision Centers');

    // Same component, new params: the request follows the URL, nothing else.
    await visit('/accounts/1/week/2026-07-20');
    flushAccounts();
    http.expectOne('/api/accounts/1/pulse?week=2026-07-20').flush(pulse(1, '2026-07-20', '2026-07-13', null));
    await settle();
    expect(el('account-name').textContent).toContain('Summit Auto Group');
  });

  it('prev/next and the account picker navigate and update the URL', async () => {
    await visit('/accounts/6/week/2026-06-01');
    flushAccounts();
    http.expectOne('/api/accounts/6/pulse?week=2026-06-01').flush(pulse(6, '2026-06-01', '2026-05-25', '2026-06-08'));
    await settle();

    el<HTMLButtonElement>('prev-week').click();
    await settle();
    expect(router.url).toBe('/accounts/6/week/2026-05-25');
    http.expectOne('/api/accounts/6/pulse?week=2026-05-25').flush(pulse(6, '2026-05-25', '2026-05-18', '2026-06-01'));
    await settle();

    el<HTMLButtonElement>('next-week').click();
    await settle();
    expect(router.url).toBe('/accounts/6/week/2026-06-01');
    http.expectOne('/api/accounts/6/pulse?week=2026-06-01').flush(pulse(6, '2026-06-01', '2026-05-25', '2026-06-08'));
    await settle();

    // Picker → /accounts/1 → the server's default week becomes the canonical URL.
    const picker = el<HTMLSelectElement>('account-picker');
    picker.value = '1';
    picker.dispatchEvent(new Event('change'));
    await settle();
    http.expectOne('/api/accounts/1/pulse').flush(pulse(1, '2026-07-20', '2026-07-13', null));
    await settle();
    expect(router.url).toBe('/accounts/1/week/2026-07-20');
    flushAccounts();
    http.expectOne('/api/accounts/1/pulse?week=2026-07-20').flush(pulse(1, '2026-07-20', '2026-07-13', null));
    await settle();

    // At the last complete week, "next" is disabled.
    expect(el<HTMLButtonElement>('next-week').disabled).toBe(true);
    expect(el<HTMLButtonElement>('prev-week').disabled).toBe(false);
  });
});
