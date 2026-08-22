using Certiflow.Notifications.Application.Abstractions;
using Certiflow.Notifications.Domain;
using Certiflow.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.Notifications.Infrastructure.Persistence;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<SupplierContactRecord> Contacts => Set<SupplierContactRecord>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new NotificationConfiguration());
        modelBuilder.ApplyConfiguration(new SupplierContactConfiguration());
        modelBuilder.AddMessagingTables();
    }
}

/// <summary>
/// Supplier contact details, fed by BC1's events (SRS §12: a read model, not a query to BC1).
/// </summary>
public sealed class SupplierContactRecord
{
    private SupplierContactRecord()
    {
        LegalName = null!;
        Email = null!;
        ContactName = null!;
    }

    public Guid SupplierId { get; private set; }

    public string LegalName { get; private set; }

    public string Email { get; private set; }

    public string ContactName { get; private set; }

    public static SupplierContactRecord From(SupplierContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        return new SupplierContactRecord
        {
            SupplierId = contact.SupplierId.Value,
            LegalName = contact.LegalName,
            Email = contact.Email,
            ContactName = contact.ContactName,
        };
    }

    public void Update(SupplierContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);

        LegalName = contact.LegalName;
        Email = contact.Email;
        ContactName = contact.ContactName;
    }

    public SupplierContact ToContact() =>
        new(new SupplierId(SupplierId), LegalName, Email, ContactName);
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Id)
            .HasColumnName("notification_id")
            .HasConversion(id => id.Value, value => new NotificationId(value))
            .ValueGeneratedNever();

        builder.Property(n => n.SupplierId)
            .HasColumnName("supplier_id")
            .HasConversion(id => id.Value, value => new SupplierId(value));

        builder.Property(n => n.DeduplicationKey).HasColumnName("deduplication_key").HasMaxLength(256).IsRequired();
        builder.Property(n => n.Recipient).HasColumnName("recipient").HasMaxLength(256).IsRequired();
        builder.Property(n => n.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(60);
        builder.Property(n => n.Subject).HasColumnName("subject").HasMaxLength(300).IsRequired();
        builder.Property(n => n.Body).HasColumnName("body").HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(n => n.Channel).HasColumnName("channel").HasConversion<string>().HasMaxLength(20);
        builder.Property(n => n.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(n => n.RaisedAt).HasColumnName("raised_at");
        builder.Property(n => n.DeliveredAt).HasColumnName("delivered_at");
        builder.Property(n => n.ReadAt).HasColumnName("read_at");
        builder.Property(n => n.FailureReason).HasColumnName("failure_reason").HasMaxLength(1000);

        builder.Ignore(n => n.DomainEvents);

        // THE guarantee behind FR-7.5, and the reason it is a unique index rather than a check in
        // application code: "does one already exist?" followed by an insert is two statements, and
        // two deliveries of the same event - or two replicas - both pass the check and both insert.
        // Only the database can refuse the second one.
        builder.HasIndex(n => n.DeduplicationKey).IsUnique().HasDatabaseName("ux_notifications_dedup");

        // The inbox view: newest first for one supplier (FR-7.4).
        builder.HasIndex(n => new { n.SupplierId, n.RaisedAt }).HasDatabaseName("ix_notifications_supplier");
    }
}

internal sealed class SupplierContactConfiguration : IEntityTypeConfiguration<SupplierContactRecord>
{
    public void Configure(EntityTypeBuilder<SupplierContactRecord> builder)
    {
        builder.ToTable("supplier_contacts");
        builder.HasKey(c => c.SupplierId);

        builder.Property(c => c.SupplierId).HasColumnName("supplier_id").ValueGeneratedNever();
        builder.Property(c => c.LegalName).HasColumnName("legal_name").HasMaxLength(200).IsRequired();
        builder.Property(c => c.Email).HasColumnName("email").HasMaxLength(256).IsRequired();
        builder.Property(c => c.ContactName).HasColumnName("contact_name").HasMaxLength(200).IsRequired();
    }
}
