using Certiflow.SharedKernel;

namespace Certiflow.Compliance.Domain;

/// <summary>
/// One supplier's obligation to evidence one Requirement. An entity inside the
/// <see cref="SupplierComplianceState"/> aggregate — it is never loaded or saved on its own.
/// </summary>
public sealed class Obligation : Entity<RequirementId>
{
    private readonly List<RetiredEvidence> _history = [];

    internal Obligation(RequirementSpecification specification)
        : base(specification.RequirementId)
    {
        Specification = specification;
        Status = ObligationStatus.Missing;
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private Obligation()
    {
        Specification = null!;
    }

    public RequirementSpecification Specification { get; private set; }

    public string DocumentType => Specification.DocumentType;

    public bool IsMandatory => Specification.IsMandatory;

    /// <summary>
    /// False once the requirement leaves the supplier's profile.
    /// <para>
    /// The obligation is retained rather than deleted, because deleting it would take its
    /// evidence history with it and SRS §10.1 forbids losing evidence — an auditor may still ask
    /// what was held against a requirement that no longer applies. Inapplicable obligations are
    /// excluded from status derivation, so they cannot affect compliance either way.
    /// </para>
    /// </summary>
    public bool IsApplicable { get; private set; } = true;

    /// <summary>
    /// The last evaluated status.
    /// <para>
    /// This is a <b>snapshot of <see cref="StatusOn"/></b>, refreshed by evaluation and persisted
    /// only so the dashboard and list views can filter in SQL within the 500 ms budget of NFR-2.
    /// It is never assigned from outside the aggregate and there is no API that accepts a status
    /// (SRS §10.1). <see cref="StatusOn"/> is the authority; this is a cache of it.
    /// </para>
    /// </summary>
    public ObligationStatus Status { get; private set; }

    public CertificateEvidence? CurrentEvidence { get; private set; }

    /// <summary>
    /// A document submitted against this requirement with no verdict yet. Drives
    /// <see cref="ObligationStatus.AwaitingReview"/>, which is what stops a supplier who has
    /// just uploaded a certificate from reading as Missing while a reviewer works through it.
    /// </summary>
    public DocumentId? PendingDocumentId { get; private set; }

    public IReadOnlyList<RetiredEvidence> History => _history.AsReadOnly();

    /// <summary>
    /// <b>The core derivation of the product.</b> A pure function of evidence, the requirement's
    /// thresholds and a date — no clock, no database, no side effects. SRS §10.1:
    /// <list type="bullet">
    /// <item>Satisfied only with approved evidence valid today <em>and</em> at least
    /// <c>MinValidityDays</c> remaining.</item>
    /// <item>AtRisk while valid but inside the renewal window.</item>
    /// <item>Expired once the validity period has ended, Missing if nothing was ever supplied.</item>
    /// </list>
    /// </summary>
    public ObligationStatus StatusOn(DateOnly today)
    {
        if (CurrentEvidence is null)
        {
            return PendingDocumentId is null
                ? ObligationStatus.Missing
                : ObligationStatus.AwaitingReview;
        }

        if (!CurrentEvidence.Validity.IsValidOn(today))
        {
            return ObligationStatus.Expired;
        }

        var daysRemaining = CurrentEvidence.Validity.DaysRemaining(today);

        // Two distinct reasons to be At Risk, deliberately collapsed into one status: the
        // certificate is inside its renewal window, or it does not have enough life left to
        // count as satisfying the requirement at all.
        var insideRenewalWindow = daysRemaining <= Specification.RenewalLeadTimeDays;
        var belowMinimumValidity = daysRemaining < Specification.MinValidityDays;

        return insideRenewalWindow || belowMinimumValidity
            ? ObligationStatus.AtRisk
            : ObligationStatus.Satisfied;
    }

    /// <summary>
    /// Binds approved evidence to this obligation. Any evidence already held is retired to
    /// history rather than overwritten — SRS §10.1 forbids deleting evidence, because an
    /// auditor asks "what did you rely on in March?" and the answer must survive a renewal.
    /// </summary>
    internal void Attach(CertificateEvidence evidence, DateOnly today, DateTimeOffset now)
    {
        Guard.AgainstNull(evidence, "compliance.obligation.evidence_required");

        Guard.Against(
            CurrentEvidence?.DocumentId == evidence.DocumentId,
            "compliance.obligation.evidence_already_attached",
            $"Document {evidence.DocumentId} is already the current evidence for requirement {Id}.");

        if (CurrentEvidence is not null)
        {
            _history.Add(new RetiredEvidence(CurrentEvidence, EvidenceRetirementReason.Superseded, now));
        }

        CurrentEvidence = evidence;

        // The submission that produced this evidence is resolved, whichever document it was.
        PendingDocumentId = null;

        Refresh(today);
    }

    /// <summary>A document was submitted and is awaiting a verdict (BC2 → BC5 on DocumentStored).</summary>
    internal void MarkAwaitingReview(DocumentId documentId, DateOnly today)
    {
        PendingDocumentId = documentId;
        Refresh(today);
    }

    /// <summary>
    /// The submission was rejected, quarantined or superseded without ever being approved. The
    /// obligation falls back to whatever its evidence says — which may still be Satisfied, since
    /// a failed renewal does not invalidate the certificate currently in force.
    /// </summary>
    internal void ClearPending(DateOnly today)
    {
        PendingDocumentId = null;
        Refresh(today);
    }

    /// <summary>
    /// Applies a new profile version's thresholds. Existing evidence is untouched: publishing a
    /// profile version must not retroactively invalidate evidence (FR-1.4). Only the derived
    /// status can change, and it changes because the rules changed, which is legitimate.
    /// </summary>
    internal void ApplySpecification(RequirementSpecification specification, DateOnly today)
    {
        Specification = Guard.AgainstNull(specification, "compliance.obligation.specification_required");

        // A requirement dropped by one profile version and restored by a later one comes back
        // with whatever evidence it still holds, rather than starting from Missing.
        IsApplicable = true;

        Refresh(today);
    }

    /// <summary>
    /// The requirement left the supplier's profile. Current evidence is moved to history and the
    /// obligation stops counting toward compliance, but the record survives — see
    /// <see cref="IsApplicable"/>.
    /// </summary>
    internal void RetireForRemovedRequirement(DateTimeOffset now)
    {
        if (CurrentEvidence is not null)
        {
            _history.Add(new RetiredEvidence(
                CurrentEvidence, EvidenceRetirementReason.RequirementNoLongerApplicable, now));
            CurrentEvidence = null;
        }

        PendingDocumentId = null;
        IsApplicable = false;
        Status = ObligationStatus.Missing;
    }

    /// <summary>Recomputes the persisted snapshot. Returns the status it held before.</summary>
    internal ObligationStatus Refresh(DateOnly today)
    {
        var previous = Status;
        Status = StatusOn(today);
        return previous;
    }

    /// <summary>Days until the current evidence expires, or null when there is none.</summary>
    public int? DaysRemaining(DateOnly today) => CurrentEvidence?.Validity.DaysRemaining(today);
}
