using Certiflow.SharedKernel;

namespace Certiflow.SupplierRegistry.Domain.Events;

public sealed record SupplierRegistered(
    SupplierId SupplierId,
    string LegalName,
    string? TradingName,
    CategoryId? CategoryId,
    string CountryCode) : DomainEvent;

public sealed record SupplierActivated(
    SupplierId SupplierId,
    CategoryId CategoryId) : DomainEvent;

public sealed record SupplierSuspended(
    SupplierId SupplierId,
    string Reason) : DomainEvent;

public sealed record SupplierOffboarded(
    SupplierId SupplierId,
    string Reason) : DomainEvent;

public sealed record SupplierCategoryChanged(
    SupplierId SupplierId,
    CategoryId PreviousCategoryId,
    CategoryId NewCategoryId) : DomainEvent;

/// <summary>One requirement as it appears in a published profile version.</summary>
public sealed record PublishedRequirement(
    RequirementId RequirementId,
    string DocumentType,
    bool IsMandatory,
    int RenewalLeadTimeDays,
    int MinValidityDays,
    bool RequiresIssuerMatch,
    IReadOnlyList<string> AcceptedIssuers,
    decimal AutoAcceptThreshold);

public sealed record ComplianceProfileVersionPublished(
    CategoryId CategoryId,
    string CategoryName,
    int ProfileVersion,
    IReadOnlyList<PublishedRequirement> Requirements) : DomainEvent;
