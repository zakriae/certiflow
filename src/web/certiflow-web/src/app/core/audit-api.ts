import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface AuditEntry {
  readonly entryId: number;
  readonly occurredAt: string;
  readonly actor: string;
  readonly action: string;
  readonly entityType: string;
  readonly entityId: string;
  readonly correlationId: string;
  readonly entryHash: string;
  readonly previousHash: string;
}

export interface ChainVerification {
  readonly isValid: boolean;
  readonly entriesVerified: number;
  readonly firstBrokenEntryId: number | null;
  readonly breakKind: string;
  readonly detail: string | null;
}

export interface Notification {
  readonly notificationId: string;
  readonly supplierId: string;
  readonly kind: string;
  readonly subject: string;
  readonly body: string;
  readonly recipient: string;
  readonly channel: string;
  readonly status: string;
  readonly raisedAt: string;
  readonly readAt: string | null;
}

@Injectable({ providedIn: 'root' })
export class AuditApi {
  private readonly http = inject(HttpClient);

  entries(take = 100): Observable<readonly AuditEntry[]> {
    return this.http.get<readonly AuditEntry[]>(`/api/audit?take=${take}`);
  }

  verifyChain(): Observable<ChainVerification> {
    return this.http.get<ChainVerification>('/api/audit/verify-chain');
  }

  notifications(): Observable<readonly Notification[]> {
    return this.http.get<readonly Notification[]>('/api/notifications');
  }

  markRead(id: string): Observable<void> {
    return this.http.post<void>(`/api/notifications/${id}/read`, {});
  }
}
