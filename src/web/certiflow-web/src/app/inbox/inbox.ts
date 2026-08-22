import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { AuditApi, Notification } from '../core/audit-api';

/**
 * The in-app inbox (FR-7.4), which is where mail goes because FR-7.8 keeps outbound email off.
 *
 * Worth being explicit about on screen: a message marked "held" was never sent. A demo that showed
 * these as delivered would be claiming something untrue about a system that is deliberately unable
 * to send anything.
 */
@Component({
  selector: 'app-inbox',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './inbox.html',
  styleUrl: './inbox.scss',
})
export class Inbox {
  private readonly api = inject(AuditApi);

  protected readonly notifications = signal<readonly Notification[]>([]);
  protected readonly expanded = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.api.notifications().subscribe({
      next: (notifications) => this.notifications.set(notifications),
      error: () => this.error.set('The inbox could not be loaded.'),
    });
  }

  protected toggle(notification: Notification): void {
    if (this.expanded() === notification.notificationId) {
      this.expanded.set(null);
      return;
    }

    this.expanded.set(notification.notificationId);

    if (notification.readAt === null) {
      this.api.markRead(notification.notificationId).subscribe({ next: () => this.load() });
    }
  }
}
