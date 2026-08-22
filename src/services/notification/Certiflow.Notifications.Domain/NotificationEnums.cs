namespace Certiflow.Notifications.Domain;

/// <summary>What happened, in the language a recipient cares about rather than the publisher's.</summary>
public enum NotificationKind
{
    DocumentApproved = 1,

    DocumentRejected = 2,

    /// <summary>A renewal reminder at one of the three windows (FR-7.2).</summary>
    CertificateExpiringSoon = 3,

    CertificateExpired = 4,

    /// <summary>A mandatory requirement has no approved evidence (FR-7.1).</summary>
    RequirementMissing = 5,

    /// <summary>To administrators, not suppliers (FR-7.3).</summary>
    SupplierBecameNonCompliant = 6,

    /// <summary>An async report finished generating (FR-6.4).</summary>
    ReportReady = 7,
}

public enum DeliveryChannel
{
    /// <summary>
    /// The default, and the reason FR-7.8 exists: a public demo that can send real mail to arbitrary
    /// addresses is an abuse vector, so mail is off unless someone deliberately turns it on.
    /// </summary>
    InApp = 1,

    Email = 2,
}

public enum DeliveryStatus
{
    Pending = 1,

    Delivered = 2,

    Failed = 3,

    /// <summary>
    /// Held rather than sent, because outbound email is disabled (FR-7.8). Distinct from Delivered
    /// on purpose: the demo inbox should be able to show what <i>would</i> have been emailed without
    /// claiming it was.
    /// </summary>
    Suppressed = 4,
}
