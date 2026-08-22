using Certiflow.SharedKernel;

namespace Certiflow.Notifications.Domain;

/// <summary>
/// One of the three renewal reminders: T-60, T-30, T-7 (FR-7.2).
/// <para>
/// A value object rather than an int, because the window — not the day count — is what makes
/// deduplication meaningful. The expiry sweep raises <c>CertificateExpiringSoon</c> every night
/// while a certificate sits inside its renewal window, so a naive "email on the event" sends a
/// supplier fifty-three identical reminders. FR-7.5 asks for one per document per window, <b>ever</b>,
/// and the window is the half of that key which is not obvious.
/// </para>
/// </summary>
public readonly record struct ReminderWindow
{
    /// <summary>Descending, because <see cref="For"/> returns the first window a count has crossed.</summary>
    private static readonly int[] Thresholds = [60, 30, 7];

    private ReminderWindow(int days) => Days = days;

    public int Days { get; }

    public static ReminderWindow SixtyDays => new(60);

    public static ReminderWindow ThirtyDays => new(30);

    public static ReminderWindow SevenDays => new(7);

    /// <summary>
    /// The window a certificate with <paramref name="daysRemaining"/> left belongs to, or null if it
    /// is not near enough to expiry to warrant one.
    /// <para>
    /// Returns the <i>tightest</i> window crossed, not the widest: a certificate first seen with 5
    /// days left has crossed all three, and sending it a "60 days remaining" notice would be absurd.
    /// A supplier who uploads late gets the urgent reminder, once, and never the earlier ones.
    /// </para>
    /// </summary>
    public static ReminderWindow? For(int daysRemaining)
    {
        // Already expired is not a reminder - it is a breach, and CertificateExpired says so.
        if (daysRemaining < 0)
        {
            return null;
        }

        foreach (var threshold in Thresholds.OrderBy(t => t))
        {
            if (daysRemaining <= threshold)
            {
                return new ReminderWindow(threshold);
            }
        }

        return null;
    }

    public override string ToString() => $"T-{Days}";
}
