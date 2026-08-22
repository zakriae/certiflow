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
    // Suppliers upload; reviewers and admins can too, which is what lets one person demonstrate
    // segregation of duties failing.
    path: 'upload',
    title: 'Upload - Certiflow',
    canActivate: [roleGuard('SupplierUser', 'Reviewer', 'Admin')],
    loadComponent: () => import('./upload/upload-screen').then((m) => m.UploadScreen),
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
  {
    // FR-5.2, and the drill-down the dashboard was missing: the portfolio says who is failing, this
    // says why and what proves it.
    path: 'suppliers/:id',
    title: 'Supplier - Certiflow',
    canActivate: [authGuard],
    loadComponent: () => import('./supplier/supplier-detail').then((m) => m.SupplierDetail),
  },
  {
    path: 'audit',
    title: 'Audit trail - Certiflow',
    canActivate: [roleGuard('Auditor', 'Reviewer', 'Admin')],
    loadComponent: () => import('./audit/audit-trail').then((m) => m.AuditTrail),
  },
  {
    path: 'inbox',
    title: 'Notifications - Certiflow',
    canActivate: [authGuard],
    loadComponent: () => import('./inbox/inbox').then((m) => m.Inbox),
  },
  { path: '**', redirectTo: 'dashboard' },
];
