import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NgxExtendedPdfViewerModule } from 'ngx-extended-pdf-viewer';
import { Auth } from '../core/auth';
import { FieldReview, ReviewApi, ReviewTaskDetail } from '../core/review-api';

/**
 * The review screen: document on one side, extracted fields on the other.
 *
 * The defining interaction is FR-4.3 - clicking a field jumps the preview to the page its citation
 * came from. That single behaviour is what turns "the AI extracted an expiry date" into "and here
 * is the sentence it read it from", which is the entire grounding story made visible. Without it
 * the citations are just text nobody checks.
 */
@Component({
  selector: 'app-review-screen',
  standalone: true,
  imports: [DecimalPipe, FormsModule, NgxExtendedPdfViewerModule],
  templateUrl: './review-screen.html',
  styleUrl: './review-screen.scss',
})
export class ReviewScreen {
  private readonly api = inject(ReviewApi);

  private readonly auth = inject(Auth);

  /**
   * The signed-in user, no longer a hard-coded demo identity.
   *
   * This matters for more than tidiness: the segregation-of-duties rule compares the approver
   * against the uploader, and while every reviewer claimed to be reviewer@certiflow.demo the rule
   * could only ever be demonstrated, never actually exercised by two different people.
   *
   * It is still a value the client sends. The server should read it from the token instead - the
   * approver's identity is not something a caller should get to name - and that is the remaining
   * step now that a token exists to read it from.
   */
  protected readonly reviewerId = computed(() => this.auth.current()?.email ?? 'unknown@certiflow.demo');

  protected readonly task = signal<ReviewTaskDetail | null>(null);
  protected readonly documentUrl = signal<string | null>(null);
  protected readonly selectedField = signal<string | null>(null);
  protected readonly page = signal(1);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly edits = signal<Record<string, string>>({});

  /** The queue, so the screen is usable without a dashboard yet. */
  protected readonly queue = signal<readonly { reviewTaskId: string; documentType: string; overallConfidence: number }[]>([]);

  protected readonly mandatoryOutstanding = computed(
    () => this.task()?.fields.filter((f) => f.isMandatory && !f.acceptedValue).length ?? 0,
  );

  /**
   * Approval is offered only when the server would allow it. The server enforces both gates
   * regardless - this just avoids inviting a click that is going to 409.
   */
  protected readonly canApprove = computed(
    () => this.task() !== null && this.mandatoryOutstanding() === 0 && this.task()!.status !== 'Completed',
  );

  constructor() {
    this.loadQueue();
  }

  protected loadQueue(): void {
    this.api.queue().subscribe({
      next: (tasks) => {
        this.queue.set(tasks);

        if (tasks.length > 0 && !this.task()) {
          this.open(tasks[0].reviewTaskId);
        }
      },
      error: () => this.error.set('Could not load the review queue. Is the verification service running?'),
    });
  }

  protected open(taskId: string): void {
    this.error.set(null);
    this.selectedField.set(null);

    this.api.task(taskId).subscribe({
      next: (task) => {
        this.task.set(task);
        this.edits.set(
          Object.fromEntries(task.fields.map((f) => [f.fieldName, f.acceptedValue ?? f.suggestedValue ?? ''])),
        );

        // Fetched per open, never cached: a SAS URL held in state outlives its expiry.
        this.api.documentLink(task.documentId).subscribe({
          next: (link) => this.documentUrl.set(link.url),
          error: () => this.error.set('The document could not be opened.'),
        });
      },
      error: () => this.error.set('Could not load that review task.'),
    });
  }

  /** FR-4.3: selecting a field takes the preview to the page its citation names. */
  protected selectField(field: FieldReview): void {
    this.selectedField.set(field.fieldName);

    if (field.citation) {
      this.page.set(field.citation.page);
    }
  }

  protected confidenceBand(confidence: number): 'good' | 'warn' | 'bad' {
    if (confidence === 0) {
      return 'bad';
    }

    return confidence >= 0.85 ? 'good' : 'warn';
  }

  protected editValue(fieldName: string): string {
    return this.edits()[fieldName] ?? '';
  }

  protected onEdit(fieldName: string, value: string): void {
    this.edits.update((current) => ({ ...current, [fieldName]: value }));
  }

  /**
   * Accepting and correcting are the same call. From the domain's point of view they are the same
   * act - a reviewer stating the value - and only the audit trail cares which it was.
   */
  protected resolve(field: FieldReview): void {
    const task = this.task();
    const value = this.editValue(field.fieldName);

    if (!task || !value) {
      return;
    }

    this.busy.set(true);

    this.api.resolveField(task.reviewTaskId, field.fieldName, value, this.reviewerId()).subscribe({
      next: () => {
        this.busy.set(false);
        this.open(task.reviewTaskId);
      },
      error: (response) => this.fail(response),
    });
  }

  protected approve(): void {
    const task = this.task();

    if (!task) {
      return;
    }

    this.busy.set(true);

    this.api.approve(task.reviewTaskId, this.reviewerId()).subscribe({
      next: () => {
        this.busy.set(false);
        this.loadQueue();
        this.open(task.reviewTaskId);
      },
      error: (response) => this.fail(response),
    });
  }

  protected reject(): void {
    const task = this.task();

    if (!task) {
      return;
    }

    this.busy.set(true);

    this.api.reject(task.reviewTaskId, this.reviewerId(), 'Illegible', null).subscribe({
      next: () => {
        this.busy.set(false);
        this.loadQueue();
        this.open(task.reviewTaskId);
      },
      error: (response) => this.fail(response),
    });
  }

  /**
   * Surfaces the server's rule code and message rather than "an error occurred". A reviewer told
   * they cannot approve their own upload learns the rule; a reviewer told nothing learns to
   * distrust the tool.
   */
  private fail(response: { error?: { detail?: string; rule?: string } }): void {
    this.busy.set(false);

    const detail = response.error?.detail ?? 'The request was refused.';
    const rule = response.error?.rule;

    this.error.set(rule ? `${detail} (${rule})` : detail);
  }
}
