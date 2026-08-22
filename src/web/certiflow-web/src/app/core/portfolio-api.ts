import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, forkJoin, map } from 'rxjs';

export interface ExpiringObligation {
  readonly supplierId: string;
  readonly requirementId: string;
  readonly documentType: string;
  readonly expiresOn: string;
  readonly daysRemaining: number;
  readonly status: string;
}

export interface NonCompliantSupplier {
  readonly supplierId: string;
  readonly breached: readonly { readonly documentType: string; readonly status: string }[];
}

export interface Dashboard {
  readonly evaluatedOn: string;
  readonly totals: Readonly<Record<string, number>>;
  readonly expiringSoon: readonly ExpiringObligation[];
  readonly nonCompliant: readonly NonCompliantSupplier[];
}

export interface SupplierStanding {
  readonly supplierId: string;
  readonly categoryId: string | null;
  readonly profileVersion: number;
  readonly overallStatus: string;
  readonly mandatoryTotal: number;
  readonly mandatorySatisfied: number;
  readonly lastEvaluatedAt: string | null;
}

export interface RegisteredSupplier {
  readonly supplierId: string;
  readonly legalName: string;
  readonly categoryId: string | null;
  readonly status: string;
}

/** The dashboard with supplier ids resolved to names, and every supplier's standing. */
export interface PortfolioView extends Dashboard {
  readonly names: ReadonlyMap<string, string>;
  readonly suppliers: readonly SupplierStanding[];
}

export interface ReportSummary {
  readonly reportId: string;
  readonly status: string;
  readonly requestedBy: string;
  readonly requestedAt: string;
  readonly completedAt: string | null;
  readonly verificationHash: string | null;
}

@Injectable({ providedIn: 'root' })
export class PortfolioApi {
  private readonly http = inject(HttpClient);

  /**
   * Compliance answers "who is non-compliant" and Registry answers "what are they called" — the
   * two are joined here rather than in a service.
   *
   * Deliberate: a supplier's legal name is BC1's, and having BC5 carry a copy so a dashboard reads
   * prettily is how a read model starts. The dashboard is a screen, it already makes one call, and
   * two parallel calls it composes itself cost nothing a user can perceive.
   */
  portfolio(): Observable<PortfolioView> {
    return forkJoin({
      dashboard: this.http.get<Dashboard>('/api/dashboard'),
      registered: this.http.get<readonly RegisteredSupplier[]>('/api/registry/suppliers'),
      standings: this.http.get<readonly SupplierStanding[]>('/api/suppliers'),
    }).pipe(
      map(({ dashboard, registered, standings }) => ({
        ...dashboard,
        names: new Map(registered.map((s) => [s.supplierId, s.legalName])),
        suppliers: standings,
      })),
    );
  }

  reportsFor(supplierId: string): Observable<readonly ReportSummary[]> {
    return this.http.get<readonly ReportSummary[]>(`/api/reports/suppliers/${supplierId}`);
  }

  // The requester comes from the token: their name is printed on the certificate and recorded in
  // the audit trail, so it is not theirs to choose.
  requestReport(supplierId: string): Observable<{ readonly reportId: string }> {
    return this.http.post<{ readonly reportId: string }>(`/api/reports/suppliers/${supplierId}`, {});
  }

  report(reportId: string): Observable<ReportSummary> {
    return this.http.get<ReportSummary>(`/api/reports/${reportId}`);
  }

  downloadUrl(reportId: string): Observable<{ readonly url: string }> {
    return this.http.get<{ readonly url: string }>(`/api/reports/${reportId}/download`);
  }
}
