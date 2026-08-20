using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.Persistence;

/// <summary>
/// The EF mappings for the outbox and inbox tables, identical in every service.
/// <para>
/// Shared because they are plumbing, not policy. Each context still owns its own schema, its own
/// <c>DbContext</c> and its own migrations — this only stops the same two table definitions being
/// retyped eight times and drifting apart at the seventh.
/// </para>
/// </summary>
public static class MessagingConfiguration
{
    /// <summary>Adds the outbox and inbox tables to a context's model.</summary>
    public static ModelBuilder AddMessagingTables(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());

        return modelBuilder;
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox");
        builder.HasKey(m => m.EventId);

        builder.Property(m => m.EventId).HasColumnName("event_id").ValueGeneratedNever();
        builder.Property(m => m.CorrelationId).HasColumnName("correlation_id");
        builder.Property(m => m.EventType).HasColumnName("event_type").HasMaxLength(300).IsRequired();
        builder.Property(m => m.PayloadJson).HasColumnName("payload_json").IsRequired();
        builder.Property(m => m.OccurredAt).HasColumnName("occurred_at");
        builder.Property(m => m.PublishedAt).HasColumnName("published_at");
        builder.Property(m => m.PublishAttempts).HasColumnName("publish_attempts");
        builder.Property(m => m.LastError).HasColumnName("last_error").HasMaxLength(2000);

        // The dispatcher only ever asks for unpublished messages in order, so the index covers
        // exactly that query and ignores every row that has already gone out.
        builder.HasIndex(m => m.OccurredAt)
            .HasDatabaseName("ix_outbox_pending")
            .HasFilter("[published_at] IS NULL");
    }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("inbox");
        builder.HasKey(m => m.MessageId);

        builder.Property(m => m.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        builder.Property(m => m.MessageType).HasColumnName("message_type").HasMaxLength(300).IsRequired();
        builder.Property(m => m.ReceivedAt).HasColumnName("received_at");
    }
}
