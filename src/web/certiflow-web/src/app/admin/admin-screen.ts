import { Component, inject, signal } from '@angular/core';
import { AdminApi, RequirementInput } from '../core/admin-api';

/**
 * Where suppliers and compliance profiles are created (FR-1.1, FR-1.2, FR-1.3).
 *
 * Everything here had an API and no screen, which meant the only way to add a supplier was curl.
 * A system whose setup step is a shell command is a system nobody can be handed.
 */
@Component({
  selector: 'app-admin-screen',
  standalone: true,
  templateUrl: './admin-screen.html',
  styleUrl: './admin-screen.scss',
})
export class AdminScreen {
  private readonly api = inject(AdminApi);

  protected readonly legalName = signal('');
  protected readonly tradingName = signal('');
  protected readonly registrationNumber = signal('');
  protected readonly countryCode = signal('GB');
  protected readonly categoryId = signal('dddd1111-eeee-2222-ffff-333344445555');
  protected readonly contactName = signal('');
  protected readonly contactEmail = signal('');
  protected readonly supplierBusy = signal(false);
  protected readonly supplierResult = signal<string | null>(null);
  protected readonly supplierError = signal<string | null>(null);

  protected readonly profileName = signal('Logistics');
  protected readonly requirements = signal<RequirementInput[]>([
    { documentType: 'ISO 9001', isMandatory: true, renewalLeadTimeDays: 60, minValidityDays: 30, requiresIssuerMatch: false, acceptedIssuers: null },
  ]);
  protected readonly profileBusy = signal(false);
  protected readonly profileResult = signal<string | null>(null);
  protected readonly profileError = signal<string | null>(null);

  protected addRequirement(): void {
    this.requirements.update((list) => [
      ...list,
      { documentType: '', isMandatory: true, renewalLeadTimeDays: 60, minValidityDays: 30, requiresIssuerMatch: false, acceptedIssuers: null },
    ]);
  }

  protected removeRequirement(index: number): void {
    this.requirements.update((list) => list.filter((_, i) => i !== index));
  }

  protected updateRequirement(index: number, patch: Partial<RequirementInput>): void {
    this.requirements.update((list) => list.map((r, i) => (i === index ? { ...r, ...patch } : r)));
  }

  protected registerSupplier(): void {
    this.supplierBusy.set(true);
    this.supplierError.set(null);
    this.supplierResult.set(null);

    this.api
      .registerSupplier({
        legalName: this.legalName().trim(),
        tradingName: this.tradingName().trim() || null,
        registrationNumber: this.registrationNumber().trim(),
        countryCode: this.countryCode().trim().toUpperCase(),
        categoryId: this.categoryId().trim() || null,
        contactName: this.contactName().trim(),
        contactEmail: this.contactEmail().trim(),
      })
      .subscribe({
        next: ({ supplierId }) => {
          this.supplierBusy.set(false);
          this.supplierResult.set(supplierId);
          this.legalName.set('');
          this.registrationNumber.set('');
          this.contactName.set('');
          this.contactEmail.set('');
        },
        error: (response) => {
          this.supplierBusy.set(false);
          // Field-level messages from the validation pipeline, not "an error occurred". The server
          // returns every failure at once for exactly this reason.
          this.supplierError.set(this.describe(response));
        },
      });
  }

  protected publishProfile(): void {
    this.profileBusy.set(true);
    this.profileError.set(null);
    this.profileResult.set(null);

    this.api.publishProfile(this.categoryId().trim(), this.profileName().trim(), this.requirements()).subscribe({
      next: ({ publishedVersion }) => {
        this.profileBusy.set(false);
        this.profileResult.set(`Published version ${publishedVersion}.`);
      },
      error: (response) => {
        this.profileBusy.set(false);
        this.profileError.set(this.describe(response));
      },
    });
  }

  private describe(response: unknown): string {
    const problem = (response as { error?: { errors?: Record<string, string[]>; detail?: string } }).error;

    if (problem?.errors) {
      return Object.entries(problem.errors)
        .map(([field, messages]) => `${field}: ${messages.join(' ')}`)
        .join(' · ');
    }

    return problem?.detail ?? 'The request was refused.';
  }
}
