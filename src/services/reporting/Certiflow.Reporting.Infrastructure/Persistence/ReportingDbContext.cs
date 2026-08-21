using Certiflow.Persistence;
using Certiflow.Reporting.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.Reporting.Infrastructure.Persistence;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options)
    : DbContext(options), IOutboxContext
{
    public const string Schema = "reporting";

    public DbSet<Report> Reports => Set<Report>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    Task<int> IOutboxContext.SaveChangesAsync(CancellationToken cancellationToken) =>
        SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new ReportConfiguration());
        modelBuilder.AddMessagingTables();
    }
}

internal sealed class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> builder)
    {
        builder.ToTable("reports");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasColumnName("report_id")
            .HasConversion(id => id.Value, value => new ReportId(value))
            .ValueGeneratedNever();

        builder.Property(r => r.Subject)
            .HasColumnName("supplier_id")
            .HasConversion(id => id.Value, value => new SupplierId(value));

        builder.Property(r => r.Type).HasColumnName("report_type").HasConversion<string>().HasMaxLength(60);
        builder.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.RequestedBy).HasColumnName("requested_by").HasMaxLength(256).IsRequired();
        builder.Property(r => r.RequestedAt).HasColumnName("requested_at");
        builder.Property(r => r.CompletedAt).HasColumnName("completed_at");
        builder.Property(r => r.VerificationHash).HasColumnName("verification_hash").HasMaxLength(64);
        builder.Property(r => r.FailureReason).HasColumnName("failure_reason").HasMaxLength(1000);

        // Owned rather than two loose columns: a container without a blob path is not a location,
        // and the aggregate should not be able to hold half of one.
        builder.OwnsOne(r => r.Storage, storage =>
        {
            storage.Property(s => s.Container).HasColumnName("storage_container").HasMaxLength(120);
            storage.Property(s => s.BlobPath).HasColumnName("storage_blob_path").HasMaxLength(500);
        });

        builder.Ignore(r => r.DomainEvents);

        // "Every report for this supplier, newest first" is the only listing the API offers.
        builder.HasIndex(r => new { r.Subject, r.RequestedAt }).HasDatabaseName("ix_reports_supplier");
    }
}
