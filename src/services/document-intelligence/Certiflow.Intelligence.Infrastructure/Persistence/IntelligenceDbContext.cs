using Certiflow.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.Intelligence.Infrastructure.Persistence;

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
        modelBuilder.AddMessagingTables();
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
