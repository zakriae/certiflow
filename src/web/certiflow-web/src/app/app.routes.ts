import { Routes } from '@angular/router';
import { authGuard, roleGuard } from './core/auth-guard';

export const routes: Routes = [
  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  {
    path: 'sign-in',
    title: 'Sign in - Certiflow',
    loadComponent: () => import('./auth/sign-in').then((m) => m.SignIn),
  },
  {
    path: 'dashboard',
    title: 'Portfolio - Certiflow',
    canActivate: [authGuard],
    loadComponent: () => import('./dashboard/dashboard').then((m) => m.Dashboard),
  },
  {
    // Lazy-loaded on purpose. The PDF viewer is roughly three quarters of a megabyte, and a
    // dashboard user who never opens a review should not pay for it on first paint.
    path: 'review',
    title: 'Review queue - Certiflow',
    // Reviewers and admins only. The server refuses everyone else regardless (ADR-0007); this
    // stops an auditor being shown an approve button that was always going to 403.
    canActivate: [roleGuard('Reviewer', 'Admin')],
    loadComponent: () => import('./review/review-screen').then((m) => m.ReviewScreen),
  },
  { path: '**', redirectTo: 'dashboard' },
];
