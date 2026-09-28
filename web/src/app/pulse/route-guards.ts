import { inject } from '@angular/core';
import { CanActivateFn, RedirectCommand, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';

import { PulseApi } from './pulse-api';

/** '' → /accounts/{first account}. */
export const firstAccount: CanActivateFn = () => {
  const router = inject(Router);
  return inject(PulseApi)
    .accounts()
    .pipe(
      map((accounts) =>
        accounts.length > 0
          ? new RedirectCommand(router.createUrlTree(['/accounts', accounts[0].id]), { replaceUrl: true })
          : true,
      ),
      catchError(() => of(true)),
    );
};

/**
 * /accounts/:id → /accounts/:id/week/{default week}, replacing the URL. The default week is the
 * server's (last complete week of the data), never computed here. If the server can't name one
 * (unknown account, no data loaded), the view renders that state from the same request.
 */
export const defaultWeek: CanActivateFn = (route) => {
  const router = inject(Router);
  const id = route.paramMap.get('id')!;
  return inject(PulseApi)
    .pulse(id)
    .pipe(
      map((pulse) =>
        pulse.period
          ? new RedirectCommand(router.createUrlTree(['/accounts', id, 'week', pulse.period.weekStart]), {
              replaceUrl: true,
            })
          : true,
      ),
      catchError(() => of(true)),
    );
};
