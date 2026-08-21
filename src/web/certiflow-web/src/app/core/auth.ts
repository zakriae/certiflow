import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';

export interface DemoAccount {
  readonly email: string;
  readonly displayName: string;
  readonly role: string;
}

export interface DemoAccounts {
  readonly password: string;
  readonly accounts: readonly DemoAccount[];
}

interface IssuedToken {
  readonly accessToken: string;
  readonly tokenType: string;
  readonly expiresIn: number;
  readonly email: string;
  readonly displayName: string;
  readonly role: string;
}

export interface Session {
  readonly token: string;
  readonly email: string;
  readonly displayName: string;
  readonly role: string;
  readonly expiresAt: number;
}

const STORAGE_KEY = 'certiflow.session';

/**
 * Who is signed in, and the token that proves it.
 *
 * Held in sessionStorage rather than localStorage: closing the tab ends the session. For a demo
 * against a seeded issuer whose signing key dies with the gateway process, a token that outlives
 * the browser tab is a token that will be rejected the next time anyone tries it — better to be
 * asked to sign in than to be told, mysteriously, that a valid-looking session is unauthorised.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);

  private readonly session = signal<Session | null>(restore());

  readonly current = this.session.asReadonly();

  readonly isSignedIn = computed(() => {
    const session = this.session();
    return session !== null && session.expiresAt > Date.now();
  });

  readonly role = computed(() => this.session()?.role ?? null);

  readonly displayName = computed(() => this.session()?.displayName ?? null);

  /** True for the roles allowed to decide a document. Mirrors the server policy; never replaces it. */
  readonly canReview = computed(() => ['Reviewer', 'Admin'].includes(this.role() ?? ''));

  readonly canAdminister = computed(() => this.role() === 'Admin');

  demoAccounts(): Observable<DemoAccounts> {
    return this.http.get<DemoAccounts>('/auth/demo-accounts');
  }

  signIn(email: string, password: string): Observable<IssuedToken> {
    return this.http.post<IssuedToken>('/auth/token', { email, password }).pipe(
      tap((issued) => {
        const session: Session = {
          token: issued.accessToken,
          email: issued.email,
          displayName: issued.displayName,
          role: issued.role,
          // Stored as an absolute instant. Keeping the server's "expires in N seconds" would drift
          // with every reload and quietly outlive the token it describes.
          expiresAt: Date.now() + issued.expiresIn * 1000,
        };

        sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
        this.session.set(session);
      }),
    );
  }

  signOut(): void {
    sessionStorage.removeItem(STORAGE_KEY);
    this.session.set(null);
  }

  token(): string | null {
    return this.isSignedIn() ? this.session()!.token : null;
  }
}

function restore(): Session | null {
  const raw = sessionStorage.getItem(STORAGE_KEY);

  if (raw === null) {
    return null;
  }

  try {
    const session = JSON.parse(raw) as Session;

    // An expired session is discarded on load rather than carried around to fail on first request.
    if (typeof session.expiresAt !== 'number' || session.expiresAt <= Date.now()) {
      sessionStorage.removeItem(STORAGE_KEY);
      return null;
    }

    return session;
  } catch {
    sessionStorage.removeItem(STORAGE_KEY);
    return null;
  }
}
