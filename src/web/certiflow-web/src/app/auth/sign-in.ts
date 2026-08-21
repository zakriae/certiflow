import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Auth, DemoAccount } from '../core/auth';

/**
 * Sign-in, with the demo accounts printed on it (SRS §16.2).
 *
 * Printing credentials on a login screen is normally indefensible. Here it is the requirement: this
 * is a portfolio demo whose accounts are fixtures against a seeded issuer, and a demo whose
 * credentials are a secret is a demo nobody can run. The accounts are fetched from the gateway
 * rather than hard-coded in the SPA, so there is exactly one place they are defined.
 */
@Component({
  selector: 'app-sign-in',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './sign-in.html',
  styleUrl: './sign-in.scss',
})
export class SignIn {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly email = signal('reviewer@certiflow.demo');
  protected readonly password = signal('');
  protected readonly accounts = signal<readonly DemoAccount[]>([]);
  protected readonly sharedPassword = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.auth.demoAccounts().subscribe({
      next: (demo) => {
        this.accounts.set(demo.accounts);
        this.sharedPassword.set(demo.password);
        this.password.set(demo.password);
      },
      // Not fatal. The form still works if someone knows the credentials; only the convenience of
      // the printed list is lost, and saying so beats an empty panel with no explanation.
      error: () => this.error.set('The gateway is not reachable, so the demo accounts could not be listed.'),
    });
  }

  protected use(account: DemoAccount): void {
    this.email.set(account.email);
    this.password.set(this.sharedPassword() ?? '');
    this.error.set(null);
  }

  protected submit(): void {
    if (this.busy()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.auth.signIn(this.email(), this.password()).subscribe({
      next: () => {
        const returnTo = this.route.snapshot.queryParamMap.get('returnTo');
        void this.router.navigateByUrl(returnTo ?? '/dashboard');
      },
      error: () => {
        this.busy.set(false);
        this.error.set('Email or password is incorrect.');
      },
    });
  }
}
