using Certiflow.SharedKernel;

namespace Certiflow.Reporting.Domain.Events;

/// <summary>
/// A report finished generating and is available. Stays inside BC6; the Infrastructure layer
/// translates it into <c>Certiflow.Contracts.ReportGenerated</c> when it writes the outbox.
/// </summary>
public sealed record ReportCompleted(
    ReportId ReportId,
    ReportType Type,
    SupplierId Subject,
    StorageReference Storage,
    string VerificationHash,
    string RequestedBy) : DomainEvent;

/// <summary>
/// A report was accepted and is waiting to be generated. Translated into the integration event that
/// this service then consumes itself — the outbox is what makes an accepted request durable.
/// </summary>
public sealed record ReportRequested(
    ReportId ReportId,
    ReportType Type,
    SupplierId Subject,
    string RequestedBy) : DomainEvent;
