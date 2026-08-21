using Certiflow.Audit.Domain;
using Certiflow.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.Audit.Infrastructure.Persistence;

/// <summary>
/// The ledger's store.
/// <para>
/// Note what is absent: an outbox. The audit trail is the end of the line — everything publishes to
/// it and it publishes to nothing (SRS §4.3, Conformist). Giving it one would suggest otherwise.
/// </para>
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public const string Schema = "audit";

    public DbSet<AuditEntryRecord> Entries => Set<AuditEntryRecord>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
        modelBuilder.AddMessagingTables();
        // The outbox table comes with AddMessagingTables; ignoring it here would mean forking that
        // shared configuration for one context. An unused empty table is the cheaper honesty.
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntryRecord>
{
    public void Configure(EntityTypeBuilder<AuditEntryRecord> builder)
    {
        builder.ToTable("entries");
        builder.HasKey(e => e.EntryId);

        // Assigned by the domain from its predecessor, never by the database. A database-generated
        // id would be allocated outside the hash and could be reordered by a rollback.
        builder.Property(e => e.EntryId).HasColumnName("entry_id").ValueGeneratedNever();

        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at");
        builder.Property(e => e.Actor).HasColumnName("actor").HasMaxLength(256).IsRequired();
        builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(200).IsRequired();
        builder.Property(e => e.EntityType).HasColumnName("entity_type").HasMaxLength(200).IsRequired();
        builder.Property(e => e.EntityId).HasColumnName("entity_id").HasMaxLength(100).IsRequired();
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id");
        builder.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(e => e.PreviousHash).HasColumnName("previous_hash").HasMaxLength(64).IsRequired();
        builder.Property(e => e.EntryHash).HasColumnName("entry_hash").HasMaxLength(64).IsRequired();

        // The audit view filters by these (FR-8.4).
        builder.HasIndex(e => e.EntityId).HasDatabaseName("ix_audit_entity");
        builder.HasIndex(e => e.CorrelationId).HasDatabaseName("ix_audit_correlation");
        builder.HasIndex(e => e.OccurredAt).HasDatabaseName("ix_audit_occurred");
    }
}
