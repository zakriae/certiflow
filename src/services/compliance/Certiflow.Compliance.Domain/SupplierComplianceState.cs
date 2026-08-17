using Certiflow.Compliance.Domain.Events;
using Certiflow.SharedKernel;

namespace Certiflow.Compliance.Domain;

/// <summary>
/// One supplier's compliance position: the obligations its category imposes, the approved
/// evidence held against each, and the status derived from the two. One instance per supplier.
/// <para>
/// <b>This aggregate is the core of the product</b> (SRS §4.1, §10). Everything about it exists
/// to make one guarantee structurally true: <em>a compliance status can never disagree with the
/// evidence behind it</em>, because it is not stored as an independent fact — it is computed.
/// A stored status drifts the moment a certificate expires overnight and nobody runs a job
/// (SRS §19 Q12).
/// </para>
/// </summary>
public sealed class SupplierComplianceState : AggregateRoot<SupplierId>
{
    private readonly List<Obligation> _obligations = [];

    private SupplierComplianceState(SupplierId supplierId, Guid categoryId)
        : base(supplierId)
    {
        CategoryId = categoryId;
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private SupplierComplianceState()
    {
    }

    public Guid CategoryId { get; private set; }

    /// <summary>
    /// The profile version whose requirements produced <see cref="Obligations"/>. Zero means no
    /// profile has been applied yet, which is why such a supplier reads as
    /// <see cref="ComplianceStatus.Pending"/> and not, absurdly, as compliant with nothing.
    /// </summary>
    public int ProfileVersion { get; private set; }

    public DateTimeOffset? LastEvaluatedAt { get; private set; }

    public IReadOnlyList<Obligation> Obligations => _obligations.AsReadOnly();

    /// <summary>
    /// <b>Derived, never assigned.</b> The worst status across <em>mandatory</em> obligations —
    /// optional ones are tracked and chased but never make a supplier non-compliant (SRS §10.1).
    /// <para>
    /// This reads the persisted per-obligation snapshots, so it is only as fresh as the last
    /// evaluation. Use <see cref="OverallStatusOn"/> for a status that cannot be stale.
    /// </para>
    /// </summary>
    public ComplianceStatus OverallStatus =>
        ProfileVersion == 0
            ? ComplianceStatus.Pending
            : _obligations
                .Where(IsCounted)
                .Select(o => o.Status)
                .DefaultIfEmpty(ObligationStatus.Satisfied)
                .Max()
                .ToComplianceStatus();

    /// <summary>
    /// The same derivation evaluated against a date instead of the stored snapshots. This is the
    /// honest answer to "is this supplier compliant right now?" and, given a past date, to the
    /// point-in-time query of FR-5.8.
    /// </summary>
    public ComplianceStatus OverallStatusOn(DateOnly today) =>
        ProfileVersion == 0
            ? ComplianceStatus.Pending
            : _obligations
                .Where(IsCounted)
                .Select(o => o.StatusOn(today))
                .DefaultIfEmpty(ObligationStatus.Satisfied)
                .Max()
                .ToComplianceStatus();

    /// <summary>
    /// The single definition of which obligations move the needle: mandatory, and still part of
    /// the supplier's profile. Both derivations above go through this so they can never disagree.
    /// </summary>
    private static bool IsCounted(Obligation obligation) =>
        obligation.IsMandatory && obligation.IsApplicable;

    /// <summary>
    /// Created in response to BC1's <c>SupplierRegistered</c>. Deliberately starts with no
    /// obligations: the requirements arrive separately with a published profile version, and a
    /// supplier whose category has no published profile yet is Pending, not Compliant.
    /// </summary>
    public static SupplierComplianceState Register(SupplierId supplierId, Guid categoryId) =>
        new(supplierId, categoryId);

    /// <summary>
    /// Rebuilds the obligation set from a profile version (FR-1.4, FR-5.6).
    /// <para>
    /// Requirements that survive keep their evidence and their history — republishing a profile
    /// must not invalidate certificates a supplier already holds. Requirements dropped from the
    /// profile retire their evidence to history rather than deleting it. New requirements arrive
    /// as Missing, which is what correctly flips a supplier non-compliant when the rules tighten.
    /// </para>
    /// </summary>
    public void ApplyProfileVersion(
        int profileVersion,
        IReadOnlyCollection<RequirementSpecification> requirements,
        DateOnly today,
        DateTimeOffset now)
    {
        Guard.AgainstNull(requirements, "compliance.profile.requirements_required");

        Guard.Require(
            profileVersion > 0,
            "compliance.profile.version_must_be_positive",
            $"Profile version must be positive, but was {profileVersion}.");

        // Out-of-order delivery is expected: Service Bus is at-least-once and gives no global
        // ordering guarantee across sessions. Applying an older profile over a newer one would
        // silently roll the rules back, so an older version is ignored rather than trusted.
        if (profileVersion <= ProfileVersion)
        {
            return;
        }

        var previousOverall = OverallStatus;
        var previousStatuses = SnapshotStatuses();

        var incoming = requirements.ToDictionary(r => r.RequirementId);

        // Retired, not removed: deleting the obligation would delete its evidence history too.
        foreach (var dropped in _obligations.Where(o => o.IsApplicable && !incoming.ContainsKey(o.Id)))
        {
            dropped.RetireForRemovedRequirement(now);
        }

        foreach (var specification in requirements)
        {
            var existing = _obligations.SingleOrDefault(o => o.Id == specification.RequirementId);

            if (existing is null)
            {
                _obligations.Add(new Obligation(specification));
            }
            else
            {
                existing.ApplySpecification(specification, today);
            }
        }

        ProfileVersion = profileVersion;

        RefreshAll(today);
        EmitTransitions(previousStatuses, previousOverall, today, now);
    }

    /// <summary>
    /// Moves an obligation to AwaitingReview because a document was submitted against it
    /// (BC2 <c>DocumentStored</c> → BC5). This is the only reason a supplier who has just
    /// uploaded a certificate does not read as Missing while a reviewer works through the queue.
    /// </summary>
    public void RecordSubmission(RequirementId requirementId, DocumentId documentId, DateOnly today, DateTimeOffset now)
    {
        var obligation = RequireObligation(requirementId);

        var previousOverall = OverallStatus;
        var previousStatuses = SnapshotStatuses();

        obligation.MarkAwaitingReview(documentId, today);

        EmitTransitions(previousStatuses, previousOverall, today, now);
    }

    /// <summary>
    /// The event that makes evidence count (BC4 <c>DocumentApproved</c> → BC5).
    /// <para>
    /// Note there is no overload that takes an extraction result. Only an approved verdict can
    /// reach this method, which is the whole reason the human-in-the-loop step exists: however
    /// confident the model was, a machine reading of a certificate is not compliance evidence
    /// until a person with the authority to be wrong has said so (SRS §4.3, §9.1).
    /// </para>
    /// </summary>
    public void ApplyApprovedEvidence(
        RequirementId requirementId,
        CertificateEvidence evidence,
        DateOnly today,
        DateTimeOffset now)
    {
        var obligation = RequireObligation(requirementId);

        var previousOverall = OverallStatus;
        var previousStatuses = SnapshotStatuses();

        obligation.Attach(evidence, today, now);

        EmitTransitions(previousStatuses, previousOverall, today, now);
    }

    /// <summary>
    /// A submission ended without approval — rejected, quarantined or superseded. The obligation
    /// falls back to whatever its existing evidence says, which may still be Satisfied: a failed
    /// renewal does not invalidate the certificate currently in force.
    /// </summary>
    public void ClearSubmission(RequirementId requirementId, DateOnly today, DateTimeOffset now)
    {
        var obligation = RequireObligation(requirementId);

        var previousOverall = OverallStatus;
        var previousStatuses = SnapshotStatuses();

        obligation.ClearPending(today);

        EmitTransitions(previousStatuses, previousOverall, today, now);
    }

    /// <summary>
    /// The Expiry Watch (FR-5.4). Re-derives every obligation against <paramref name="today"/>
    /// and raises exactly the transitions that occurred — nothing when nothing changed, which is
    /// what keeps a nightly sweep over every supplier from becoming a nightly flood of email.
    /// <para>
    /// Idempotent by construction: running it twice on the same date produces events the first
    /// time and none the second, because the second run finds no transitions.
    /// </para>
    /// </summary>
    public void Evaluate(DateOnly today, DateTimeOffset now)
    {
        var previousOverall = OverallStatus;
        var previousStatuses = SnapshotStatuses();

        RefreshAll(today);
        EmitTransitions(previousStatuses, previousOverall, today, now);
    }

    public Obligation? FindObligation(RequirementId requirementId) =>
        _obligations.SingleOrDefault(o => o.Id == requirementId);

    private Obligation RequireObligation(RequirementId requirementId)
    {
        var obligation = FindObligation(requirementId);

        // A document submitted against a requirement this supplier's category does not impose is
        // a genuine inconsistency between BC1 and BC5, not a routine miss. Failing loudly here
        // sends the message to the DLQ (NFR-6) where it is visible, rather than dropping it.
        // A retired obligation is treated the same way: it is kept for its history, not to
        // accept new evidence against a requirement that no longer applies.
        if (obligation is null || !obligation.IsApplicable)
        {
            throw new DomainRuleViolationException(
                "compliance.obligation.not_in_profile",
                $"Supplier {Id} has no active obligation for requirement {requirementId} in profile version {ProfileVersion}.");
        }

        return obligation;
    }

    private Dictionary<RequirementId, ObligationStatus> SnapshotStatuses() =>
        _obligations.ToDictionary(o => o.Id, o => o.Status);

    private void RefreshAll(DateOnly today)
    {
        foreach (var obligation in _obligations.Where(o => o.IsApplicable))
        {
            obligation.Refresh(today);
        }
    }

    /// <summary>
    /// Compares the pre-change snapshot against current state and raises one event per real
    /// transition. Centralising this is what makes every mutator above three lines long and
    /// guarantees no code path can change a status without announcing it.
    /// </summary>
    private void EmitTransitions(
        Dictionary<RequirementId, ObligationStatus> previousStatuses,
        ComplianceStatus previousOverall,
        DateOnly today,
        DateTimeOffset now)
    {
        LastEvaluatedAt = now;

        foreach (var obligation in _obligations.Where(o => o.IsApplicable))
        {
            // A brand-new obligation has no previous status; treat it as Missing so a newly
            // published mandatory requirement reports a breach rather than passing silently.
            var previous = previousStatuses.GetValueOrDefault(obligation.Id, ObligationStatus.Missing);
            var current = obligation.Status;

            if (previous == current)
            {
                continue;
            }

            EmitObligationTransition(obligation, previous, current, today);
        }

        var newOverall = OverallStatus;

        if (newOverall != previousOverall)
        {
            Raise(new ComplianceStatusChanged(Id, previousOverall, newOverall, today));

            if (newOverall == ComplianceStatus.NonCompliant)
            {
                var breached = _obligations
                    .Where(o => IsCounted(o) && o.Status is ObligationStatus.Expired or ObligationStatus.Missing)
                    .Select(o => o.Id)
                    .ToList();

                Raise(new SupplierBecameNonCompliant(Id, breached, today));
            }
        }
    }

    private void EmitObligationTransition(
        Obligation obligation,
        ObligationStatus previous,
        ObligationStatus current,
        DateOnly today)
    {
        switch (current)
        {
            case ObligationStatus.Satisfied when obligation.CurrentEvidence is not null:
                Raise(new ObligationSatisfied(Id, obligation.Id, obligation.CurrentEvidence.DocumentId));
                break;

            // Raised on the transition into the renewal window only. Re-announcing it on every
            // nightly sweep is how a reminder system trains its users to ignore it (FR-7.5).
            case ObligationStatus.AtRisk when obligation.CurrentEvidence is not null:
                Raise(new CertificateExpiringSoon(
                    Id,
                    obligation.Id,
                    obligation.CurrentEvidence.DocumentId,
                    obligation.CurrentEvidence.Validity.ExpiresOn,
                    obligation.CurrentEvidence.Validity.DaysRemaining(today)));
                break;

            case ObligationStatus.Expired when obligation.CurrentEvidence is not null:
                Raise(new CertificateExpired(
                    Id,
                    obligation.Id,
                    obligation.CurrentEvidence.DocumentId,
                    obligation.CurrentEvidence.Validity.ExpiresOn));
                break;

            default:
                break;
        }

        // The enum is ordered best-to-worst, so "got worse" is a comparison. Improvements are
        // announced by the cases above; only regressions are a breach.
        if (current > previous)
        {
            Raise(new ObligationBreached(Id, obligation.Id, previous, current));
        }
    }
}
