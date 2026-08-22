import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { AuditApi, AuditEntry, ChainVerification } from '../core/audit-api';

/**
 * The audit trail, and the screen that makes the hash chain mean something to a viewer.
 *
 * A list of events is unremarkable — every system has one. What is worth showing is the
 * verification: the chain recomputed on demand, and the fact that altering a single row anywhere in
 * its history is detected and located. That is the difference between a log and a ledger, and it is
 * invisible until someone presses the button.
 */
@Component({
  selector: 'app-audit-trail',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './audit-trail.html',
  styleUrl: './audit-trail.scss',
})
export class AuditTrail {
  private readonly api = inject(AuditApi);

  protected readonly entries = signal<readonly AuditEntry[]>([]);
  protected readonly verification = signal<ChainVerification | null>(null);
  protected readonly verifying = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.api.entries().subscribe({
      next: (entries) => this.entries.set(entries),
      error: () => this.error.set('The audit trail could not be loaded.'),
    });
  }

  protected verify(): void {
    this.verifying.set(true);
    this.error.set(null);

    this.api.verifyChain().subscribe({
      next: (result) => {
        this.verification.set(result);
        this.verifying.set(false);
      },
      error: () => {
        this.verifying.set(false);
        this.error.set('The chain could not be verified.');
      },
    });
  }

  /** The row a broken chain points at, so it can be highlighted rather than described. */
  protected isBroken(entry: AuditEntry): boolean {
    const result = this.verification();
    return result !== null && !result.isValid && result.firstBrokenEntryId === entry.entryId;
  }

  /**
   * Scrolls the broken row into view.
   *
   * Entries are newest first and a break is usually old, so the highlighted row sat far below the
   * fold: the screen said "broken at entry 2" and then showed twenty rows that were all fine. Being
   * told where the damage is and having to go looking for it is a poor way to make the point.
   */
  protected showBrokenEntry(): void {
    const result = this.verification();

    if (result?.firstBrokenEntryId == null) {
      return;
    }

    document
      .getElementById(`entry-${result.firstBrokenEntryId}`)
      ?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }
}
