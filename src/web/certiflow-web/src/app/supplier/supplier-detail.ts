import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { PortfolioApi, ReportSummary, SupplierCompliance } from '../core/portfolio-api';

/**
 * One supplier, obligation by obligation (FR-5.2).
 *
 * The dashboard answers "who is failing"; this answers "why, and what proves it". Each obligation
 * shows the evidence satisfying it — certificate number, issuer, who approved it and when — because
 * a compliance status with no evidence behind it is an assertion, and the whole point of the system
 * is that its assertions are backed.
 */
@Component({
  selector: 'app-supplier-detail',
  standalone: true,
  imports: [DatePipe, RouterLink],
  templateUrl: './supplier-detail.html',
  styleUrl: './supplier-detail.scss',
})
export class SupplierDetail {
  private readonly api = inject(PortfolioApi);
  private readonly route = inject(ActivatedRoute);

  protected readonly supplierId = signal('');
  protected readonly name = signal('');
  protected readonly compliance = signal<SupplierCompliance | null>(null);
  protected readonly reports = signal<readonly ReportSummary[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    const id = this.route.snapshot.paramMap.get('id') ?? '';
    this.supplierId.set(id);
    this.load();
  }

  protected load(): void {
    const id = this.supplierId();

    this.api.compliance(id).subscribe({
      next: (state) => this.compliance.set(state),
      error: () => this.error.set('That supplier has no compliance record.'),
    });

    // The name lives in BC1 and the status in BC5, joined here for the same reason the dashboard
    // does it: giving BC5 a copy of a supplier's name so a screen reads well is how a read model
    // starts, and this screen already makes more than one call.
    this.api.supplier(id).subscribe({
      next: (supplier) => this.name.set(supplier.legalName),
      error: () => this.name.set(id),
    });

    this.api.reportsFor(id).subscribe({ next: (reports) => this.reports.set(reports) });
  }

  protected requestReport(): void {
    this.busy.set(true);

    this.api.requestReport(this.supplierId()).subscribe({
      next: ({ reportId }) => this.poll(reportId, 0),
      error: () => {
        this.busy.set(false);
        this.error.set('The report could not be requested.');
      },
    });
  }

  private poll(reportId: string, attempt: number): void {
    if (attempt > 20) {
      this.busy.set(false);
      this.error.set('The report is taking longer than expected.');
      return;
    }

    setTimeout(() => {
      this.api.report(reportId).subscribe({
        next: (report) => {
          if (report.status === 'Completed' || report.status === 'Failed') {
            this.busy.set(false);
            this.api.reportsFor(this.supplierId()).subscribe({ next: (r) => this.reports.set(r) });
            return;
          }

          this.poll(reportId, attempt + 1);
        },
        error: () => this.busy.set(false),
      });
    }, 1000);
  }

  protected download(reportId: string): void {
    this.api.downloadUrl(reportId).subscribe({
      next: ({ url }) => window.open(url, '_blank', 'noopener'),
      error: () => this.error.set('The report could not be downloaded.'),
    });
  }
}
