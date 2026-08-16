using Certiflow.SharedKernel;

namespace Certiflow.Verification.Domain;

public enum VerdictDecision
{
    Approved = 1,
    Rejected = 2,
}

/// <summary>
/// The controlled list a rejection must come from (FR-4.6).
/// <para>
/// Controlled rather than free text for two reasons: the supplier is told the reason verbatim and
/// needs to know what to actually do about it, and rejection reasons are the only usable signal
/// for where extraction quality is failing. Free text gives you neither — it gives you 400 ways
/// of writing "wrong document".
/// </para>
/// </summary>
public enum RejectionReason
{
    /// <summary>The scan is unreadable.</summary>
    Illegible = 1,

    /// <summary>The certificate had already expired when it was submitted.</summary>
    AlreadyExpired = 2,

    /// <summary>A valid document, but not the one this requirement asks for.</summary>
    WrongDocumentType = 3,

    /// <summary>Issued to a different legal entity than the supplier on record.</summary>
    HolderMismatch = 4,

    /// <summary>The issuing body is not accepted for this requirement.</summary>
    IssuerNotAccepted = 5,

    /// <summary>Pages or mandatory details are missing.</summary>
    Incomplete = 6,

    /// <summary>Signs of tampering. Deliberately distinct from Illegible — it escalates.</summary>
    SuspectedForgery = 7,

    /// <summary>Anything else. Requires a note, or it is indistinguishable from no reason at all.</summary>
    Other = 99,
}

/// <summary>
/// The immutable outcome of a Review Task (SRS §3).
/// <para>
/// Write-once by design. A verdict recorded in error is not edited — the supplier submits a new
/// document, which supersedes the old one and produces a new task and a new verdict. Editing
/// history is precisely what an auditor is checking for, and a system that permits it cannot
/// answer "who approved this, and when" (SRS §9.1).
/// </para>
/// </summary>
public sealed record Verdict
{
    private Verdict(
        VerdictDecision decision,
        RejectionReason? reason,
        string? reasonNote,
        string decidedBy,
        DateTimeOffset decidedAt)
    {
        Decision = decision;
        Reason = reason;
        ReasonNote = reasonNote;
        DecidedBy = decidedBy;
        DecidedAt = decidedAt;
    }

    public VerdictDecision Decision { get; }

    /// <summary>Always present on a rejection, always absent on an approval.</summary>
    public RejectionReason? Reason { get; }

    public string? ReasonNote { get; }

    public string DecidedBy { get; }

    public DateTimeOffset DecidedAt { get; }

    public static Verdict Approve(string decidedBy, DateTimeOffset decidedAt) =>
        new(
            VerdictDecision.Approved,
            reason: null,
            reasonNote: null,
            Guard.AgainstNullOrWhiteSpace(decidedBy, "verification.verdict.decider_required"),
            decidedAt);

    public static Verdict Reject(
        RejectionReason reason,
        string? reasonNote,
        string decidedBy,
        DateTimeOffset decidedAt)
    {
        // "Other" without an explanation is the same as no reason at all, and it is the reason a
        // supplier will phone about.
        Guard.Require(
            reason != RejectionReason.Other || !string.IsNullOrWhiteSpace(reasonNote),
            "verification.verdict.other_requires_note",
            "Rejecting with reason 'Other' requires an explanatory note.");

        return new Verdict(
            VerdictDecision.Rejected,
            reason,
            reasonNote?.Trim(),
            Guard.AgainstNullOrWhiteSpace(decidedBy, "verification.verdict.decider_required"),
            decidedAt);
    }
}
