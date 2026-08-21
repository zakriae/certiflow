using Certiflow.Reporting.Domain.Events;
using Certiflow.SharedKernel;

namespace Certiflow.Reporting.Domain;

/// <summary>
/// One generation job and the immutable artefact it produced (FR-6.4, FR-6.5).
/// <para>
/// A report is never regenerated in place. Re-running produces a new <see cref="ReportId"/> with its
/// own blob and its own fingerprint, so a report someone downloaded last March still says what it
/// said in March even after the supplier's position changes. That is the difference between an
/// attestation and a dashboard.
/// </para>
/// </summary>
public sealed class Report : AggregateRoot<ReportId>
{
    private Report(ReportId id, ReportType type, SupplierId subject, string requestedBy, DateTimeOffset requestedAt)
    {
        Id = id;
        Type = type;
        Subject = subject;
        RequestedBy = requestedBy;
        RequestedAt = requestedAt;
        Status = ReportStatus.Requested;
    }

    public ReportType Type { get; private set; }

    /// <summary>The supplier the report is about. Portfolio reports (FR-6.2) are not built.</summary>
    public SupplierId Subject { get; private set; }

    public ReportStatus Status { get; private set; }

    public string RequestedBy { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public StorageReference? Storage { get; private set; }

    /// <summary>Set once, when generation succeeds. See <see cref="ReportFingerprint"/>.</summary>
    public string? VerificationHash { get; private set; }

    public string? FailureReason { get; private set; }

    public static Report Request(ReportType type, SupplierId subject, string requestedBy, DateTimeOffset now)
    {
        Guard.AgainstNullOrWhiteSpace(requestedBy, "reporting.report.requested_by_required");

        var report = new Report(ReportId.New(), type, subject, requestedBy.Trim(), now);

        report.Raise(new ReportRequested(report.Id, type, subject, report.RequestedBy));

        return report;
    }

    public void Start()
    {
        // Requested -> Generating is the only legal entry. A redelivered ReportRequested message
        // must not restart a job that already finished and overwrite a completed report with a
        // second, differently-dated one; the caller treats false as "already handled".
        if (Status != ReportStatus.Requested)
        {
            throw new DomainRuleViolationException(
                "reporting.report.already_started",
                $"Report {Id} is {Status} and cannot be started again.");
        }

        Status = ReportStatus.Generating;
    }

    public void Complete(StorageReference storage, string verificationHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Guard.AgainstNullOrWhiteSpace(verificationHash, "reporting.report.verification_hash_required");

        if (Status is not (ReportStatus.Generating or ReportStatus.Requested))
        {
            throw new DomainRuleViolationException(
                "reporting.report.not_in_progress",
                $"Report {Id} is {Status} and cannot be completed.");
        }

        Status = ReportStatus.Completed;
        Storage = storage;
        VerificationHash = verificationHash;
        CompletedAt = now;

        Raise(new ReportCompleted(Id, Type, Subject, storage, verificationHash, RequestedBy));
    }

    /// <summary>
    /// Failure is a terminal state that is recorded, not thrown away. A caller who asked for a
    /// report and got silence has no way to tell "still working" from "gave up an hour ago".
    /// </summary>
    public void Fail(string reason, DateTimeOffset now)
    {
        Guard.AgainstNullOrWhiteSpace(reason, "reporting.report.failure_reason_required");

        if (Status == ReportStatus.Completed)
        {
            throw new DomainRuleViolationException(
                "reporting.report.already_completed",
                $"Report {Id} completed at {CompletedAt:O} and cannot be marked failed.");
        }

        Status = ReportStatus.Failed;
        FailureReason = reason;
        CompletedAt = now;
    }
}

/// <summary>Where the rendered PDF lives. Reports get their own container (SRS §13.2).</summary>
public sealed record StorageReference(string Container, string BlobPath)
{
    public static StorageReference Create(string container, string blobPath)
    {
        Guard.AgainstNullOrWhiteSpace(container, "reporting.storage.container_required");
        Guard.AgainstNullOrWhiteSpace(blobPath, "reporting.storage.blob_path_required");

        return new StorageReference(container, blobPath);
    }
}
