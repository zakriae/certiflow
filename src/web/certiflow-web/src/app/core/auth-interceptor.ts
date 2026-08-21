import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { Auth } from './auth';

/**
 * Attaches the bearer token to every call, and treats a 401 as "your session is over".
 *
 * The sign-in call itself is excluded — sending a stale token to the endpoint whose job is to
 * replace it achieves nothing, and a 401 from it would sign the user out of the session they are
 * trying to start.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(Auth);
  const router = inject(Router);

  const isAuthEndpoint = request.url.startsWith('/auth/token') || request.url.startsWith('/auth/demo-accounts');
  const token = auth.token();

  const outbound = token !== null && !isAuthEndpoint
    ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : request;

  return next(outbound).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && !isAuthEndpoint) {
        // The token expired, or the gateway restarted and threw away the key that signed it.
        // Either way the session is dead and pretending otherwise produces a screen of failed
        // requests with no explanation.
        auth.signOut();
        void router.navigate(['/sign-in']);
      }

      // 403 is deliberately not handled here. It means the caller is authenticated and not
      // permitted, which is a real answer the component should show rather than a session problem.
      return throwError(() => error);
    }),
  );
};
