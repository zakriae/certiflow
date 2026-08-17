using Certiflow.SharedKernel;

namespace Certiflow.Compliance.Domain.Events;

/// <summary>
/// Domain events raised by <see cref="SupplierComplianceState"/>. These stay inside BC5 and are
/// expressed in BC5's own types; the Infrastructure layer translates them into the integration
/// events of <c>Certiflow.Contracts</c> when it writes the outbox (SRS §3, §12). The names are
/// intentionally the same on both sides — the shape is not.
/// </summary>
public sealed record ObligationSatisfied(
    SupplierId SupplierId,
    RequirementId RequirementId,
    DocumentId DocumentId) : DomainEvent;

/// <summary>
/// An obligation moved to a worse status. Carries both statuses so a consumer never has to ask
/// "worse than what?", and so the audit trail can render the transition (FR-8.5).
/// </summary>
public sealed record ObligationBreached(
    SupplierId SupplierId,
    RequirementId RequirementId,
    ObligationStatus PreviousStatus,
    ObligationStatus NewStatus) : DomainEvent;

public sealed record ComplianceStatusChanged(
    SupplierId SupplierId,
    ComplianceStatus PreviousStatus,
    ComplianceStatus NewStatus,
    DateOnly EvaluatedOn) : DomainEvent;

/// <summary>
/// Raised once, on the transition into the renewal window — not on every nightly evaluation.
/// Re-raising it every night is how reminder systems become noise, and FR-7.5 requires one
/// reminder per document per window, ever.
/// </summary>
public sealed record CertificateExpiringSoon(
    SupplierId SupplierId,
    RequirementId RequirementId,
    DocumentId DocumentId,
    DateOnly ExpiresOn,
    int DaysRemaining) : DomainEvent;

public sealed record CertificateExpired(
    SupplierId SupplierId,
    RequirementId RequirementId,
    DocumentId DocumentId,
    DateOnly ExpiredOn) : DomainEvent;

/// <summary>
/// The headline event for the dashboard and for admin alerting (FR-7.3). Strictly redundant
/// given <see cref="ComplianceStatusChanged"/>, and kept anyway: BC7 subscribing to
/// "became non-compliant" is far clearer than BC7 filtering status transitions.
/// </summary>
public sealed record SupplierBecameNonCompliant(
    SupplierId SupplierId,
    IReadOnlyList<RequirementId> BreachedRequirements,
    DateOnly EvaluatedOn) : DomainEvent;
