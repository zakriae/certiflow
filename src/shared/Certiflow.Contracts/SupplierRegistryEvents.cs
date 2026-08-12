namespace Certiflow.Contracts;

/// <summary>
/// Published by BC1 Supplier Registry. Consumed by BC5 Compliance and BC8 Audit (SRS §12).
/// </summary>
public sealed record SupplierRegistered(
    Guid SupplierId,
    string LegalName,
    string? TradingName,
    Guid CategoryId,
    string CountryCode,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

public sealed record SupplierActivated(
    Guid SupplierId,
    Guid CategoryId,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// A suspended supplier stops generating notifications (FR-1.8), so BC7 needs this too.
/// </summary>
public sealed record SupplierSuspended(
    Guid SupplierId,
    string Reason,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// Changing category changes which Requirements apply, so BC5 must rebuild the supplier's
/// obligations from the new profile. Existing approved evidence survives — it is bound to a
/// document, not to a profile version (FR-1.4).
/// </summary>
public sealed record SupplierCategoryChanged(
    Guid SupplierId,
    Guid PreviousCategoryId,
    Guid NewCategoryId,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// The whole profile is carried in the event rather than a pointer, so BC5 never queries BC1's
/// database to evaluate compliance (SRS §4.3 Published Language).
/// </summary>
public sealed record ComplianceProfileVersionPublished(
    Guid CategoryId,
    string CategoryName,
    int ProfileVersion,
    IReadOnlyList<RequirementDescriptor> Requirements,
    Guid CorrelationId,
    Guid? CausationId = null) : IntegrationEvent(CorrelationId, CausationId);

/// <summary>
/// One Requirement as other contexts see it. Deliberately primitives and strings: a consumer
/// that binds to BC1's <c>DocumentType</c> value object would be coupled to BC1's model.
/// </summary>
public sealed record RequirementDescriptor(
    Guid RequirementId,
    string DocumentType,
    bool IsMandatory,
    int RenewalLeadTimeDays,
    int MinValidityDays,
    bool RequiresIssuerMatch,
    IReadOnlyList<string> AcceptedIssuers,
    decimal AutoAcceptThreshold);
