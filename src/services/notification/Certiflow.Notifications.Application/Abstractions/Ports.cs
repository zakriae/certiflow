using Certiflow.Notifications.Domain;

namespace Certiflow.Notifications.Application.Abstractions;

public interface INotificationRepository
{
    void Add(Notification notification);

    Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken);

    /// <summary>
    /// Persists, returning false when the deduplication key already exists.
    /// <para>
    /// Returns a result rather than throwing, because a duplicate is the <i>expected</i> outcome
    /// here, not an error: the expiry sweep raises the same event every night for weeks and every
    /// one of those after the first is supposed to be dropped.
    /// </para>
    /// </summary>
    Task<bool> SaveIfNewAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Where a supplier's email address comes from.
/// <para>
/// A read model fed by BC1's events, not an HTTP call. Unlike a compliance certificate — which is a
/// point-in-time attestation and must read through to the source (ADR-0006) — a notification is
/// fire-and-forget: if the address is a few seconds stale the mail still arrives at the address the
/// supplier had a few seconds ago, and blocking a reminder because Registry is down would be worse
/// than sending it slightly late.
/// </para>
/// </summary>
public interface ISupplierContactDirectory
{
    Task<SupplierContact?> FindAsync(SupplierId supplierId, CancellationToken cancellationToken);

    Task UpsertAsync(SupplierContact contact, CancellationToken cancellationToken);
}

public sealed record SupplierContact(SupplierId SupplierId, string LegalName, string Email, string ContactName);

/// <summary>Hands a notification to its channel. Implementations decide nothing about whether to send.</summary>
public interface INotificationSender
{
    Task<DeliveryOutcome> SendAsync(Notification notification, CancellationToken cancellationToken);
}

public enum DeliveryOutcome
{
    Delivered = 1,

    /// <summary>Deliberately not sent — outbound mail is disabled (FR-7.8).</summary>
    Suppressed = 2,

    Failed = 3,
}
