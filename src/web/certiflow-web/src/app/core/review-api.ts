import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** One field as the review screen needs it: the value, how much it is trusted, and where it came from. */
export interface FieldReview {
  readonly fieldName: string;
  readonly suggestedValue: string | null;
  readonly acceptedValue: string | null;
  readonly confidence: number;
  readonly isMandatory: boolean;
  readonly wasCorrected: boolean;
  readonly scoringNote: string | null;
  readonly reviewerNote: string | null;
  readonly resolvedBy: string | null;
  /** Null when the model returned no citation - which means the field is ungrounded and scores zero. */
  readonly citation: { readonly page: number; readonly snippet: string } | null;
}

export interface ReviewTaskSummary {
  readonly reviewTaskId: string;
  readonly documentId: string;
  readonly supplierId: string;
  readonly documentType: string;
  readonly status: string;
  readonly raisedReason: string;
  readonly priority: string;
  readonly overallConfidence: number;
  readonly assignedTo: string | null;
  readonly unresolvedMandatoryFields: number;
}

export interface ReviewTaskDetail extends ReviewTaskSummary {
  readonly extractionJobId: string;
  readonly uploadedBy: string;
  readonly canApprove: boolean;
  readonly verdict: {
    readonly decision: string;
    readonly reason: string | null;
    readonly reasonNote: string | null;
    readonly decidedBy: string;
    readonly decidedAt: string;
  } | null;
  readonly fields: readonly FieldReview[];
}

export interface DocumentLink {
  readonly url: string;
  readonly expiresInSeconds: number;
  readonly fileName: string;
}

@Injectable({ providedIn: 'root' })
export class ReviewApi {
  private readonly http = inject(HttpClient);

  queue(): Observable<readonly ReviewTaskSummary[]> {
    return this.http.get<readonly ReviewTaskSummary[]>('/api/review-tasks');
  }

  task(id: string): Observable<ReviewTaskDetail> {
    return this.http.get<ReviewTaskDetail>(`/api/review-tasks/${id}`);
  }

  /**
   * A short-lived SAS URL, fetched when the reviewer opens the document rather than stored with the
   * task. A URL that lives in application state outlives its own expiry and starts returning 403s.
   */
  documentLink(documentId: string): Observable<DocumentLink> {
    return this.http.get<DocumentLink>(`/api/documents/${documentId}/link`);
  }

  resolveField(taskId: string, fieldName: string, acceptedValue: string, reviewerId: string) {
    return this.http.post<void>(`/api/review-tasks/${taskId}/fields`, {
      fieldName,
      acceptedValue,
      reviewerId,
    });
  }

  approve(taskId: string, reviewerId: string) {
    return this.http.post<void>(`/api/review-tasks/${taskId}/approve`, { reviewerId });
  }

  reject(taskId: string, reviewerId: string, reason: string, reasonNote: string | null) {
    return this.http.post<void>(`/api/review-tasks/${taskId}/reject`, { reviewerId, reason, reasonNote });
  }
}
