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

export interface AuditFilters {
  readonly entityId?: string;
  readonly actor?: string;
  /** Dates as yyyy-MM-dd, from an <input type="date">. */
  readonly from?: string;
  readonly to?: string;
}

@Injectable({ providedIn: 'root' })
export class AuditApi {
  private readonly http = inject(HttpClient);

  entries(filters: AuditFilters = {}, take = 200): Observable<readonly AuditEntry[]> {
    const query = new URLSearchParams({ take: String(take) });

    // Only set what was actually chosen: an empty string is a filter that matches nothing, which
    // looks identical to "no results" and is far more confusing.
    if (filters.entityId) query.set('entityId', filters.entityId);
    if (filters.actor) query.set('actor', filters.actor);
    if (filters.from) query.set('from', new Date(filters.from).toISOString());
    if (filters.to) query.set('to', new Date(`${filters.to}T23:59:59`).toISOString());

    return this.http.get<readonly AuditEntry[]>(`/api/audit?${query}`);
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
