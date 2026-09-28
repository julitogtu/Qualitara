import { Routes } from '@angular/router';

import { defaultWeek, firstAccount } from './pulse/route-guards';
import { PulseView } from './pulse/pulse-view';

export const routes: Routes = [
  { path: '', pathMatch: 'full', canActivate: [firstAccount], children: [] },
  // Resolves to the canonical /accounts/:id/week/:week, replacing the URL.
  { path: 'accounts/:id', canActivate: [defaultWeek], component: PulseView },
  { path: 'accounts/:id/week/:week', component: PulseView },
  { path: '**', redirectTo: '' },
];
