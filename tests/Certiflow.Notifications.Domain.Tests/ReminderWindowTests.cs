using Certiflow.Notifications.Domain;
using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.Notifications.Domain.Tests;

public sealed class ReminderWindowTests
{
    [Theory]
    [InlineData(90, null)]
    [InlineData(61, null)]
    [InlineData(60, 60)]
    [InlineData(45, 60)]
    [InlineData(31, 60)]
    [InlineData(30, 30)]
    [InlineData(8, 30)]
    [InlineData(7, 7)]
    [InlineData(1, 7)]
    [InlineData(0, 7)]
    public void A_day_count_maps_to_the_tightest_window_it_has_crossed(int daysRemaining, int? expected)
    {
        var window = ReminderWindow.For(daysRemaining);

        window?.Days.Should().Be(expected);

        if (expected is null)
        {
            window.Should().BeNull();
        }
    }

    [Fact]
    public void A_certificate_first_seen_with_five_days_left_gets_the_urgent_reminder_not_the_early_one()
    {
        // It has technically crossed all three thresholds. Telling a supplier "60 days remaining"
        // when there are five would be worse than saying nothing.
        ReminderWindow.For(5).Should().Be(ReminderWindow.SevenDays);
    }

    [Fact]
    public void An_expired_certificate_gets_no_reminder()
    {
        // Expiry is not a reminder, it is a breach, and CertificateExpired carries that.
        ReminderWindow.For(-1).Should().BeNull();
    }

    [Fact]
    public void The_window_is_part_of_the_key_so_the_three_reminders_are_distinct()
    {
        var document = new DocumentId(Guid.Parse("dddd0000-0000-0000-0000-000000000001"));

        var keys = new[]
        {
            DeduplicationKeys.Reminder(document, ReminderWindow.SixtyDays),
            DeduplicationKeys.Reminder(document, ReminderWindow.ThirtyDays),
            DeduplicationKeys.Reminder(document, ReminderWindow.SevenDays),
        };

        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_renewal_is_notifiable_again_because_it_is_a_different_document()
    {
        // The point of keying on the document rather than the requirement: a replacement certificate
        // deserves its own three reminders, and would get none if the key were the obligation.
        var original = new DocumentId(Guid.Parse("dddd0000-0000-0000-0000-000000000001"));
        var renewal = new DocumentId(Guid.Parse("dddd0000-0000-0000-0000-000000000002"));

        DeduplicationKeys.Reminder(original, ReminderWindow.SixtyDays)
            .Should().NotBe(DeduplicationKeys.Reminder(renewal, ReminderWindow.SixtyDays));
    }

    [Fact]
    public void The_same_document_and_window_always_produce_the_same_key()
    {
        // Fifty-three nightly sweeps inside the renewal window must produce one reminder, not fifty
        // -three (FR-7.5).
        var document = new DocumentId(Guid.NewGuid());

        DeduplicationKeys.Reminder(document, ReminderWindow.ThirtyDays)
            .Should().Be(DeduplicationKeys.Reminder(document, ReminderWindow.ThirtyDays));
    }
}

public sealed class NotificationTests
{
    private static readonly SupplierId Supplier = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 0, 0, TimeSpan.Zero);

    private static Notification Raised(DeliveryChannel channel = DeliveryChannel.InApp) =>
        Notification.Raise("decision:x:DocumentApproved", Supplier, "claire@meridian.example",
            NotificationKind.DocumentApproved, "Your ISO 9001 certificate was approved",
            "The certificate you uploaded has been approved.", channel, Now);

    [Fact]
    public void A_raised_notification_is_pending_until_something_delivers_it()
    {
        var notification = Raised();

        notification.Status.Should().Be(DeliveryStatus.Pending);
        notification.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public void Suppressed_is_not_delivered_and_is_not_failed()
    {
        // FR-7.8: with outbound mail off, the message is held. The demo inbox shows what would have
        // been sent - claiming it was delivered would be a lie, and calling it a failure would send
        // someone looking for a fault that does not exist.
        var notification = Raised(DeliveryChannel.Email);

        notification.MarkSuppressed(Now);

        notification.Status.Should().Be(DeliveryStatus.Suppressed);
        notification.FailureReason.Should().BeNull();
    }

    [Fact]
    public void A_failure_records_why()
    {
        var notification = Raised(DeliveryChannel.Email);

        notification.MarkFailed("SMTP host refused the connection", Now);

        notification.Status.Should().Be(DeliveryStatus.Failed);
        notification.FailureReason.Should().Be("SMTP host refused the connection");
    }

    [Fact]
    public void Reading_a_notification_twice_does_not_move_the_timestamp()
    {
        var notification = Raised();

        notification.MarkRead(Now);
        notification.MarkRead(Now.AddHours(3));

        notification.ReadAt.Should().Be(Now);
    }

    [Fact]
    public void A_notification_must_name_a_recipient()
    {
        var act = () => Notification.Raise("k", Supplier, "  ", NotificationKind.DocumentApproved,
            "s", "b", DeliveryChannel.InApp, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("notification.recipient_required");
    }

    [Fact]
    public void A_notification_must_carry_a_deduplication_key()
    {
        // Without one there is no way to enforce "once, ever" - and the unique index would collapse
        // every notification onto a single row.
        var act = () => Notification.Raise("", Supplier, "a@b.example", NotificationKind.DocumentApproved,
            "s", "b", DeliveryChannel.InApp, Now);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("notification.deduplication_key_required");
    }
}
