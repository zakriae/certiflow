import { HttpClient, HttpEvent, HttpEventType } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

export interface UploadResult {
  readonly documentId: string;
  readonly status: string;
  readonly duplicateOfDocumentId: string | null;
}

export type UploadProgress =
  | { readonly kind: 'progress'; readonly percent: number }
  | { readonly kind: 'done'; readonly result: UploadResult };

export interface Requirement {
  readonly requirementId: string;
  readonly documentType: string;
  readonly isMandatory: boolean;
  readonly status: string;
}

@Injectable({ providedIn: 'root' })
export class UploadApi {
  private readonly http = inject(HttpClient);

  requirementsFor(supplierId: string): Observable<readonly Requirement[]> {
    return this.http
      .get<{ readonly obligations: readonly Requirement[] }>(`/api/suppliers/${supplierId}/compliance`)
      .pipe(map((state) => state.obligations));
  }

  /**
   * Reports progress rather than resolving once at the end. A 20 MB certificate on a slow
   * connection is several seconds of apparently nothing happening, and "apparently nothing" is when
   * people click the button again.
   *
   * Note what is absent: uploadedBy. The server reads the uploader from the token, because that is
   * the value the approval rule compares against.
   */
  upload(supplierId: string, requirementId: string, documentType: string, file: File): Observable<UploadProgress> {
    const form = new FormData();
    form.append('supplierId', supplierId);
    form.append('requirementId', requirementId);
    form.append('documentType', documentType);
    form.append('file', file, file.name);

    return this.http
      .post<UploadResult>('/api/documents', form, { observe: 'events', reportProgress: true })
      .pipe(
        map((event: HttpEvent<UploadResult>): UploadProgress => {
          if (event.type === HttpEventType.UploadProgress) {
            return { kind: 'progress', percent: event.total ? Math.round((100 * event.loaded) / event.total) : 0 };
          }

          if (event.type === HttpEventType.Response) {
            return { kind: 'done', result: event.body! };
          }

          return { kind: 'progress', percent: 0 };
        }),
      );
  }
}
