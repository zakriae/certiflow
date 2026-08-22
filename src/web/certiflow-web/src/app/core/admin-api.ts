import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface NewSupplier {
  readonly legalName: string;
  readonly tradingName: string | null;
  readonly registrationNumber: string;
  readonly countryCode: string;
  readonly categoryId: string | null;
  readonly contactName: string;
  readonly contactEmail: string;
}

export interface RequirementInput {
  readonly documentType: string;
  readonly isMandatory: boolean;
  readonly renewalLeadTimeDays: number;
  readonly minValidityDays: number;
  readonly requiresIssuerMatch: boolean;
  readonly acceptedIssuers: readonly string[] | null;
}

export interface Category {
  readonly categoryId: string;
  readonly name: string;
  readonly publishedVersion: number;
}

@Injectable({ providedIn: 'root' })
export class AdminApi {
  private readonly http = inject(HttpClient);

  registerSupplier(supplier: NewSupplier): Observable<{ readonly supplierId: string }> {
    return this.http.post<{ readonly supplierId: string }>('/api/registry/suppliers', supplier);
  }

  category(categoryId: string): Observable<Category> {
    return this.http.get<Category>(`/api/registry/categories/${categoryId}`);
  }

  publishProfile(categoryId: string, name: string, requirements: readonly RequirementInput[]) {
    return this.http.post<{ readonly publishedVersion: number }>(
      `/api/registry/categories/${categoryId}/profile`,
      { name, requirements },
    );
  }
}
