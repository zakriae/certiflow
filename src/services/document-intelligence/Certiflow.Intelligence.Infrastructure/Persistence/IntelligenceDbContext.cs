using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.Intelligence.Infrastructure.Persistence;

/// <summary>
/// A message this service has already handled.
/// <para>
/// <b>The inbox — the other half of at-least-once delivery.</b> The outbox guarantees an event is
/// published even if the publisher crashes, which means it can be published <em>twice</em>. This
/// table is how the consumer stays correct when that happens: the message id is inserted in the
/// same transaction as the work it authorised, so replaying a message finds the row and does
/// nothing (SRS §5.3, §19 Q6).
/// </para>
/// <para>
/// Note what it is not: a lock, a cache, or a queue. It is a record of "this exact message has been
/// dealt with", keyed on the publisher's own event id.
/// </para>
/// </summary>
public sealed class InboxMessage
{
    private InboxMessage()
    {
        MessageType = null!;
    }

    public InboxMessage(Guid messageId, string messageType, DateTimeOffset receivedAt)
    {
        MessageId = messageId;
        MessageType = messageType;
        ReceivedAt = receivedAt;
    }

    public Guid MessageId { get; private set; }

    public string MessageType { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }
}

public sealed class IntelligenceDbContext(DbContextOptions<IntelligenceDbContext> options) : DbContext(options)
{
    public const string Schema = "intelligence";

    public DbSet<ExtractionJobRecord> ExtractionJobs => Set<ExtractionJobRecord>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new ExtractionJobConfiguration());
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
    }
}

internal sealed class ExtractionJobConfiguration : IEntityTypeConfiguration<ExtractionJobRecord>
{
    public void Configure(EntityTypeBuilder<ExtractionJobRecord> builder)
    {
        builder.ToTable("extraction_jobs");
        builder.HasKey(j => j.ExtractionJobId);

        builder.Property(j => j.ExtractionJobId).HasColumnName("extraction_job_id").ValueGeneratedNever();
        builder.Property(j => j.DocumentId).HasColumnName("document_id");
        builder.Property(j => j.SupplierId).HasColumnName("supplier_id");
        builder.Property(j => j.RequirementId).HasColumnName("requirement_id");
        builder.Property(j => j.DocumentType).HasColumnName("document_type").HasMaxLength(100).IsRequired();
        builder.Property(j => j.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(j => j.AttemptCount).HasColumnName("attempt_count");
        builder.Property(j => j.ModelUsed).HasColumnName("model_used").HasMaxLength(100);
        builder.Property(j => j.PromptVersion).HasColumnName("prompt_version").HasMaxLength(50);
        builder.Property(j => j.TokensConsumed).HasColumnName("tokens_consumed");
        builder.Property(j => j.OverallConfidence).HasColumnName("overall_confidence").HasPrecision(3, 2);
        builder.Property(j => j.AutoAcceptThreshold).HasColumnName("auto_accept_threshold").HasPrecision(3, 2);
        builder.Property(j => j.IsAutoAcceptable).HasColumnName("is_auto_acceptable");
        builder.Property(j => j.TextSource).HasColumnName("text_source").HasMaxLength(40);
        builder.Property(j => j.FailureReason).HasColumnName("failure_reason").HasMaxLength(1000);
        builder.Property(j => j.RecordedAt).HasColumnName("recorded_at");

        // The scored fields as JSON. They are always read as a unit with their job and never
        // queried across jobs - "every field below 0.6" is a question for the review queue's own
        // read model, not for this table.
        builder.Property(j => j.FieldsJson).HasColumnName("fields_json").HasColumnType("nvarchar(max)").IsRequired();

        builder.HasIndex(j => j.DocumentId).HasDatabaseName("ix_extraction_jobs_document");
    }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox");

        // The primary key *is* the deduplication. A replayed message fails to insert rather than
        // relying on a prior read having seen it, which closes the race between two consumers
        // handling the same redelivery at once.
        builder.HasKey(m => m.MessageId);

        builder.Property(m => m.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        builder.Property(m => m.MessageType).HasColumnName("message_type").HasMaxLength(300).IsRequired();
        builder.Property(m => m.ReceivedAt).HasColumnName("received_at");
    }
}
