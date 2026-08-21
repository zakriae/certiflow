import { Component, computed, inject, signal } from '@angular/core';
import { Auth } from '../core/auth';
import { PortfolioApi, PortfolioView, ReportSummary } from '../core/portfolio-api';

/**
 * The portfolio dashboard (FR-5.3), and the screen the demo opens on.
 *
 * It answers one question first — who is non-compliant, right now — because that is the question
 * the Compliance Manager persona actually has (SRS §2). Everything else on the page is secondary to
 * getting that answer above the fold.
 */
@Component({
  selector: 'app-dashboard',
  standalone: true,
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class Dashboard {
  private readonly api = inject(PortfolioApi);
  protected readonly auth = inject(Auth);

  protected readonly view = signal<PortfolioView | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busySupplier = signal<string | null>(null);
  protected readonly reports = signal<Record<string, readonly ReportSummary[]>>({});
  protected readonly expanded = signal<string | null>(null);

  protected readonly totals = computed(() => {
    const totals = this.view()?.totals ?? {};

    // Listed in severity order rather than whatever order the server's dictionary produced, and
    // with zeroes shown: "NonCompliant 0" is information, a missing tile is ambiguity.
    return (['NonCompliant', 'ExpiringSoon', 'Pending', 'Compliant'] as const).map((status) => ({
      status,
      label: status === 'NonCompliant' ? 'Non-compliant' : status === 'ExpiringSoon' ? 'Expiring soon' : status,
      count: totals[status] ?? 0,
    }));
  });

  constructor() {
    this.load();
  }

  protected load(): void {
    this.api.portfolio().subscribe({
      next: (view) => {
        this.view.set(view);
        this.error.set(null);
      },
      error: () => this.error.set('The portfolio could not be loaded.'),
    });
  }

  /**
   * The five most recent, not all of them. The API returns up to fifty (FR-6.5 keeps every run), and
   * an expanded row that pushes the rest of the page off the screen is not a feature — anyone
   * auditing the full history wants the reports list, not a dashboard accordion.
   */
  protected recentReports(supplierId: string): readonly ReportSummary[] {
    return (this.reports()[supplierId] ?? []).slice(0, 5);
  }

  protected reportCount(supplierId: string): number {
    return (this.reports()[supplierId] ?? []).length;
  }

  protected nameOf(supplierId: string): string {
    // Falls back to the id rather than blank. A row that names nothing is worse than a row that
    // names an id someone can search for.
    return this.view()?.names.get(supplierId) ?? supplierId;
  }

  protected toggleReports(supplierId: string): void {
    if (this.expanded() === supplierId) {
      this.expanded.set(null);
      return;
    }

    this.expanded.set(supplierId);
    this.api.reportsFor(supplierId).subscribe({
      next: (list) => this.reports.update((all) => ({ ...all, [supplierId]: list })),
    });
  }

  protected requestReport(supplierId: string): void {
    const requestedBy = this.auth.current()?.email ?? 'unknown@certiflow.demo';

    this.busySupplier.set(supplierId);

    this.api.requestReport(supplierId, requestedBy).subscribe({
      next: ({ reportId }) => this.poll(supplierId, reportId, 0),
      error: () => {
        this.busySupplier.set(null);
        this.error.set('The report could not be requested.');
      },
    });
  }

  /**
   * Generation is asynchronous (FR-6.4), so the button polls rather than waiting on a response
   * that was never going to carry the PDF. Capped, so a wedged job stops the spinner instead of
   * leaving it turning forever.
   */
  private poll(supplierId: string, reportId: string, attempt: number): void {
    if (attempt > 20) {
      this.busySupplier.set(null);
      this.error.set('The report is taking longer than expected. It will appear under Reports when it finishes.');
      return;
    }

    setTimeout(() => {
      this.api.report(reportId).subscribe({
        next: (report) => {
          if (report.status === 'Completed') {
            this.busySupplier.set(null);
            this.expanded.set(supplierId);
            this.api.reportsFor(supplierId).subscribe({
              next: (list) => this.reports.update((all) => ({ ...all, [supplierId]: list })),
            });
            return;
          }

          if (report.status === 'Failed') {
            this.busySupplier.set(null);
            this.error.set('The report failed to generate.');
            return;
          }

          this.poll(supplierId, reportId, attempt + 1);
        },
        error: () => {
          this.busySupplier.set(null);
          this.error.set('The report status could not be read.');
        },
      });
    }, 1000);
  }

  protected download(reportId: string): void {
    // The API returns a short-lived SAS rather than bytes (NFR-10), so the browser is sent to
    // storage directly instead of the PDF travelling back through the gateway.
    this.api.downloadUrl(reportId).subscribe({
      next: ({ url }) => window.open(url, '_blank', 'noopener'),
      error: () => this.error.set('The report could not be downloaded.'),
    });
  }
}
