import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { Auth } from './core/auth';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  private readonly router = inject(Router);

  protected readonly auth = inject(Auth);

  /**
   * The current URL, as a signal.
   *
   * Written as a plain subscription rather than toSignal(): toSignal threw NG0203 here at runtime
   * even though a component field initialiser is supposed to be an injection context. Chasing that
   * would be a research project about Angular internals; a subscription in the constructor is four
   * lines, has no such caveat, and the component lives for the life of the app so there is nothing
   * to unsubscribe from.
   */
  private readonly url = signal(this.router.url);

  protected readonly showChrome = computed(() => this.auth.isSignedIn() && !this.url().startsWith('/sign-in'));

  constructor() {
    this.router.events
      .pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd))
      .subscribe((event) => this.url.set(event.urlAfterRedirects));
  }

  protected signOut(): void {
    this.auth.signOut();
    void this.router.navigate(['/sign-in']);
  }
}
