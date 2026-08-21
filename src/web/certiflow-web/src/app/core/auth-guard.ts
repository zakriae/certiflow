import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Auth } from './auth';

/**
 * Keeps unauthenticated users off the app routes.
 *
 * A convenience, not a control: the guard runs in the browser and can be bypassed by anyone who
 * cares to. What actually protects the data is the gateway and the services, both of which validate
 * the token independently (ADR-0007). This exists so a signed-out user sees a sign-in screen rather
 * than a dashboard full of failed requests.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(Auth);
  const router = inject(Router);

  if (auth.isSignedIn()) {
    return true;
  }

  return router.createUrlTree(['/sign-in'], { queryParams: { returnTo: state.url } });
};

/** Routes only some roles may see. The server enforces the same rule; this only hides the door. */
export const roleGuard = (...allowed: readonly string[]): CanActivateFn => (_route, _state) => {
  const auth = inject(Auth);
  const router = inject(Router);

  if (!auth.isSignedIn()) {
    return router.createUrlTree(['/sign-in']);
  }

  return allowed.includes(auth.role() ?? '') ? true : router.createUrlTree(['/dashboard']);
};
