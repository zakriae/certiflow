import { Component, computed, inject, signal } from '@angular/core';
import { Auth } from '../core/auth';
import { PortfolioApi, SupplierStanding } from '../core/portfolio-api';
import { Requirement, UploadApi, UploadResult } from '../core/upload-api';

/**
 * Where a certificate enters the system.
 *
 * The screen the SRS demo opens with — drag a PDF, watch fields populate — and the one path
 * guardrail G1 says must never be anonymous, because it is what spends tokens at Azure OpenAI.
 */
@Component({
  selector: 'app-upload-screen',
  standalone: true,
  templateUrl: './upload-screen.html',
  styleUrl: './upload-screen.scss',
})
export class UploadScreen {
  private readonly api = inject(UploadApi);
  private readonly portfolio = inject(PortfolioApi);
  protected readonly auth = inject(Auth);

  protected readonly suppliers = signal<readonly SupplierStanding[]>([]);
  protected readonly names = signal<ReadonlyMap<string, string>>(new Map());
  protected readonly supplierId = signal<string>('');
  protected readonly requirements = signal<readonly Requirement[]>([]);
  protected readonly requirementId = signal<string>('');
  protected readonly file = signal<File | null>(null);
  protected readonly dragging = signal(false);
  protected readonly percent = signal(0);
  protected readonly busy = signal(false);
  protected readonly result = signal<UploadResult | null>(null);
  protected readonly error = signal<string | null>(null);

  protected readonly selectedRequirement = computed(() =>
    this.requirements().find((r) => r.requirementId === this.requirementId()) ?? null);

  protected readonly canSubmit = computed(() =>
    !this.busy() && this.file() !== null && this.supplierId() !== '' && this.requirementId() !== '');

  constructor() {
    this.portfolio.portfolio().subscribe({
      next: (view) => {
        this.suppliers.set(view.suppliers);
        this.names.set(view.names);

        // A supplier user has exactly one supplier to choose from, so choosing is not a decision -
        // it is a step to be removed.
        if (view.suppliers.length > 0) {
          this.chooseSupplier(view.suppliers[0].supplierId);
        }
      },
      error: () => this.error.set('Suppliers could not be loaded.'),
    });
  }

  protected nameOf(supplierId: string): string {
    return this.names().get(supplierId) ?? supplierId;
  }

  protected chooseSupplier(supplierId: string): void {
    this.supplierId.set(supplierId);
    this.requirementId.set('');
    this.result.set(null);

    this.api.requirementsFor(supplierId).subscribe({
      next: (requirements) => {
        this.requirements.set(requirements);

        // Default to something outstanding rather than the first in the list: the reason anyone
        // opens this screen is a requirement that is not yet satisfied.
        const outstanding = requirements.find((r) => r.status !== 'Satisfied') ?? requirements[0];

        if (outstanding) {
          this.requirementId.set(outstanding.requirementId);
        }
      },
      error: () => this.error.set('Requirements could not be loaded for that supplier.'),
    });
  }

  protected onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.dragging.set(true);
  }

  protected onDragLeave(): void {
    this.dragging.set(false);
  }

  protected onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragging.set(false);

    const dropped = event.dataTransfer?.files?.[0];

    if (dropped) {
      this.choose(dropped);
    }
  }

  protected onPick(event: Event): void {
    const picked = (event.target as HTMLInputElement).files?.[0];

    if (picked) {
      this.choose(picked);
    }
  }

  private choose(file: File): void {
    this.result.set(null);
    this.error.set(null);

    // Checked here and enforced again by the aggregate and the transport (guardrail G4). This one
    // is a courtesy: telling someone before a 30 MB upload rather than after.
    if (file.size > 20 * 1024 * 1024) {
      this.error.set('That file is larger than the 20 MB limit.');
      return;
    }

    if (file.type !== 'application/pdf') {
      this.error.set('Only PDF certificates can be uploaded.');
      return;
    }

    this.file.set(file);
  }

  protected submit(): void {
    const file = this.file();
    const requirement = this.selectedRequirement();

    if (!this.canSubmit() || file === null || requirement === null) {
      return;
    }

    this.busy.set(true);
    this.percent.set(0);
    this.error.set(null);

    this.api.upload(this.supplierId(), requirement.requirementId, requirement.documentType, file).subscribe({
      next: (progress) => {
        if (progress.kind === 'progress') {
          this.percent.set(progress.percent);
          return;
        }

        this.busy.set(false);
        this.result.set(progress.result);
        this.file.set(null);
      },
      error: () => {
        this.busy.set(false);
        this.error.set('The upload was refused.');
      },
    });
  }
}
