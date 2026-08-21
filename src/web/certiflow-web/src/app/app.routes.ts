import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'review', pathMatch: 'full' },
  {
    // Lazy-loaded on purpose. The PDF viewer is roughly three quarters of a megabyte, and a
    // dashboard user who never opens a review should not pay for it on first paint.
    path: 'review',
    title: 'Review queue - Certiflow',
    loadComponent: () => import('./review/review-screen').then((m) => m.ReviewScreen),
  },
];
