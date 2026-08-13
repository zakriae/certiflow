using Certiflow.SharedKernel;
using Certiflow.SupplierRegistry.Domain.Events;

namespace Certiflow.SupplierRegistry.Domain;

/// <summary>
/// A single obligation: a document type a supplier of a category must hold (SRS §3, §6.1).
/// </summary>
public sealed class Requirement : Entity<RequirementId>
{
    internal Requirement(
        RequirementId id,
        DocumentType documentType,
        bool isMandatory,
        int renewalLeadTimeDays,
        int minValidityDays,
        bool requiresIssuerMatch,
        IReadOnlyList<string> acceptedIssuers,
        decimal autoAcceptThreshold)
        : base(id)
    {
        DocumentType = Guard.AgainstNull(documentType, "registry.requirement.document_type_required");
        IsMandatory = isMandatory;

        RenewalLeadTimeDays = Guard.AgainstOutOfRange(
            renewalLeadTimeDays, 1, 365, "registry.requirement.lead_time_out_of_range");

        MinValidityDays = Guard.AgainstOutOfRange(
            minValidityDays, 0, 365, "registry.requirement.min_validity_out_of_range");

        RequiresIssuerMatch = requiresIssuerMatch;
        AcceptedIssuers = acceptedIssuers;

        AutoAcceptThreshold = Guard.AgainstOutOfRange(
            autoAcceptThreshold, 0m, 1m, "registry.requirement.threshold_out_of_range");

        Guard.Require(
            !requiresIssuerMatch || acceptedIssuers.Count > 0,
            "registry.requirement.issuer_match_without_issuers",
            $"Requirement '{documentType}' demands an issuer match but lists no accepted issuers.");
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private Requirement()
    {
        DocumentType = null!;
        AcceptedIssuers = [];
    }

    public DocumentType DocumentType { get; private set; }

    public bool IsMandatory { get; private set; }

    public int RenewalLeadTimeDays { get; private set; }

    public int MinValidityDays { get; private set; }

    public bool RequiresIssuerMatch { get; private set; }

    public IReadOnlyList<string> AcceptedIssuers { get; private set; }

    /// <summary>
    /// Per-requirement auto-accept bar, default 0.85 (SRS §8.4). Configurable because a public
    /// liability insurance certificate and a safety-training record do not deserve the same
    /// scepticism, and forcing one number on both means either needless review or needless risk.
    /// </summary>
    public decimal AutoAcceptThreshold { get; private set; }

    /// <summary>
    /// Deprecated rather than deleted (SRS §6.1). A requirement already evidenced by suppliers cannot
    /// be removed without orphaning that evidence, so it is dropped from the next profile version and
    /// the historical record stays intact.
    /// </summary>
    public bool IsDeprecated { get; private set; }

    internal void Deprecate() => IsDeprecated = true;
}

/// <summary>
/// The set of Requirements attached to a Category (SRS §3, §6.1).
/// <para>
/// Versioned, and that is the interesting part. Publishing a version is what BC5 consumes to rebuild
/// every affected supplier's obligations, so a version is an event rather than a save — and existing
/// evidence survives it, because evidence is bound to a document, not to a profile version (FR-1.4).
/// </para>
/// </summary>
public sealed class ComplianceProfile : AggregateRoot<CategoryId>
{
    /// <summary>SRS §8.4's default auto-accept threshold.</summary>
    public const decimal DefaultAutoAcceptThreshold = 0.85m;

    private readonly List<Requirement> _requirements = [];

    private ComplianceProfile(CategoryId categoryId, string name)
        : base(categoryId)
    {
        Name = name;
        PublishedVersion = 0;
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private ComplianceProfile()
    {
        Name = null!;
    }

    /// <summary>The category name, e.g. <c>Logistics Contractor</c>.</summary>
    public string Name { get; private set; }

    /// <summary>Zero until the profile is published for the first time.</summary>
    public int PublishedVersion { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Every requirement, including deprecated ones. Filter by <see cref="ActiveRequirements"/>.</summary>
    public IReadOnlyList<Requirement> Requirements => _requirements.AsReadOnly();

    public IEnumerable<Requirement> ActiveRequirements => _requirements.Where(r => !r.IsDeprecated);

    /// <summary>
    /// True when the profile has unpublished edits. The distinction matters: editing a profile must
    /// not silently change what every supplier in the category is judged against — publishing does
    /// that, deliberately and traceably.
    /// </summary>
    public bool HasUnpublishedChanges { get; private set; }

    public static ComplianceProfile Create(string name) =>
        new(CategoryId.New(), Guard.AgainstNullOrWhiteSpace(name, "registry.profile.name_required"));

    public static ComplianceProfile CreateFor(CategoryId categoryId, string name) =>
        new(categoryId, Guard.AgainstNullOrWhiteSpace(name, "registry.profile.name_required"));

    public Requirement AddRequirement(
        DocumentType documentType,
        bool isMandatory = true,
        int renewalLeadTimeDays = 60,
        int minValidityDays = 0,
        bool requiresIssuerMatch = false,
        IReadOnlyList<string>? acceptedIssuers = null,
        decimal? autoAcceptThreshold = null)
    {
        Guard.AgainstNull(documentType, "registry.requirement.document_type_required");

        // SRS §6.1: no duplicate DocumentType within a profile. Two requirements for the same document
        // type would give a supplier two obligations one certificate could satisfy, and no way to say
        // which one it satisfied.
        Guard.Against(
            ActiveRequirements.Any(r => r.DocumentType.IsSameAs(documentType)),
            "registry.profile.duplicate_document_type",
            $"This profile already requires '{documentType}'.");

        var requirement = new Requirement(
            RequirementId.New(),
            documentType,
            isMandatory,
            renewalLeadTimeDays,
            minValidityDays,
            requiresIssuerMatch,
            acceptedIssuers ?? [],
            autoAcceptThreshold ?? DefaultAutoAcceptThreshold);

        _requirements.Add(requirement);
        HasUnpublishedChanges = true;

        return requirement;
    }

    /// <summary>
    /// Retires a requirement. Never deletes: SRS §6.1 forbids removing a requirement that suppliers
    /// have evidenced, and since this aggregate cannot see who has evidenced what, it deprecates
    /// unconditionally rather than asking.
    /// </summary>
    public void DeprecateRequirement(RequirementId requirementId)
    {
        var requirement = _requirements.SingleOrDefault(r => r.Id == requirementId)
            ?? throw new DomainRuleViolationException(
                "registry.profile.unknown_requirement",
                $"Profile {Id} has no requirement {requirementId}.");

        if (requirement.IsDeprecated)
        {
            return;
        }

        requirement.Deprecate();
        HasUnpublishedChanges = true;
    }

    /// <summary>
    /// Publishes the next version, emitting the event BC5 rebuilds obligations from.
    /// <para>
    /// The whole requirement set travels in the event rather than a pointer, so Compliance never
    /// queries this service's database to evaluate a supplier (SRS §4.3, Published Language).
    /// </para>
    /// </summary>
    public void Publish(DateTimeOffset now)
    {
        var active = ActiveRequirements.ToList();

        Guard.Require(
            active.Count > 0,
            "registry.profile.nothing_to_publish",
            $"Profile '{Name}' has no active requirements; publishing it would make every supplier in the category vacuously compliant.");

        Guard.Require(
            active.Any(r => r.IsMandatory),
            "registry.profile.no_mandatory_requirements",
            $"Profile '{Name}' has no mandatory requirements, so no supplier in the category could ever be non-compliant.");

        PublishedVersion++;
        PublishedAt = now;
        HasUnpublishedChanges = false;

        Raise(new ComplianceProfileVersionPublished(
            Id,
            Name,
            PublishedVersion,
            [.. active.Select(r => new PublishedRequirement(
                r.Id,
                r.DocumentType.Value,
                r.IsMandatory,
                r.RenewalLeadTimeDays,
                r.MinValidityDays,
                r.RequiresIssuerMatch,
                r.AcceptedIssuers,
                r.AutoAcceptThreshold))]));
    }
}
