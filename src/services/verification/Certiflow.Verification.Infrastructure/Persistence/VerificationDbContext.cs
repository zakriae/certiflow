using Certiflow.Persistence;
using Certiflow.Verification.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.Verification.Infrastructure.Persistence;

public sealed class VerificationDbContext(DbContextOptions<VerificationDbContext> options)
    : DbContext(options), IOutboxContext
{
    public const string Schema = "verification";

    public DbSet<ReviewTask> ReviewTasks => Set<ReviewTask>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    /// <summary>Document metadata from Intake - see <see cref="DocumentRecord"/>.</summary>
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new ReviewTaskConfiguration());
        modelBuilder.Entity<DocumentRecord>(document =>
        {
            document.ToTable("documents");
            document.HasKey(d => d.DocumentId);
            document.Property(d => d.DocumentId).HasColumnName("document_id").ValueGeneratedNever();
            document.Property(d => d.SupplierId).HasColumnName("supplier_id");
            document.Property(d => d.FileName).HasColumnName("file_name").HasMaxLength(260).IsRequired();
            document.Property(d => d.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(256).IsRequired();
            document.Property(d => d.StoredAt).HasColumnName("stored_at");
        });

        modelBuilder.AddMessagingTables();
    }
}

internal sealed class ReviewTaskConfiguration : IEntityTypeConfiguration<ReviewTask>
{
    public void Configure(EntityTypeBuilder<ReviewTask> builder)
    {
        builder.ToTable("review_tasks");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .HasColumnName("review_task_id")
            .HasConversion(id => id.Value, value => new ReviewTaskId(value))
            .ValueGeneratedNever();

        builder.Property(t => t.DocumentId)
            .HasColumnName("document_id")
            .HasConversion(id => id.Value, value => new DocumentId(value));

        builder.Property(t => t.ExtractionJobId)
            .HasColumnName("extraction_job_id")
            .HasConversion(id => id.Value, value => new ExtractionJobId(value));

        builder.Property(t => t.SupplierId)
            .HasColumnName("supplier_id")
            .HasConversion(id => id.Value, value => new SupplierId(value));

        builder.Property(t => t.RequirementId)
            .HasColumnName("requirement_id")
            .HasConversion(id => id.Value, value => new RequirementId(value));

        builder.Property(t => t.DocumentType).HasColumnName("document_type").HasMaxLength(100);
        builder.Property(t => t.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(256);
        builder.Property(t => t.AssignedTo).HasColumnName("assigned_to").HasMaxLength(256);
        builder.Property(t => t.OverallConfidence).HasColumnName("overall_confidence").HasPrecision(3, 2);
        builder.Property(t => t.CancellationReason).HasColumnName("cancellation_reason").HasMaxLength(500);
        builder.Property(t => t.CurrentEvidenceExpiresOn).HasColumnName("current_evidence_expires_on");

        builder.Property(t => t.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.RaisedReason).HasColumnName("raised_reason").HasConversion<string>().HasMaxLength(30);

        // The verdict is a value object with no identity of its own, so it maps into the parent
        // row rather than inventing a table. Null until a decision is made, which is why every
        // column here is nullable.
        builder.OwnsOne(t => t.Verdict, verdict =>
        {
            verdict.Property(v => v.Decision).HasColumnName("verdict_decision").HasConversion<string>().HasMaxLength(20);
            verdict.Property(v => v.Reason).HasColumnName("verdict_reason").HasConversion<string>().HasMaxLength(40);
            verdict.Property(v => v.ReasonNote).HasColumnName("verdict_reason_note").HasMaxLength(1000);
            verdict.Property(v => v.DecidedBy).HasColumnName("verdict_decided_by").HasMaxLength(256);
            verdict.Property(v => v.DecidedAt).HasColumnName("verdict_decided_at");
        });

        // Field reviews are a real child table, unlike BC3's extracted fields.
        //
        // The difference is that these are *written* one at a time as a reviewer works through
        // them, and the queue needs to ask "which mandatory fields are still unresolved" across
        // tasks. Serialising them to JSON would turn every keystroke into a rewrite of the whole
        // collection and make that question unanswerable in SQL.
        builder.OwnsMany(t => t.FieldReviews, field =>
        {
            field.ToTable("field_reviews");
            field.WithOwner().HasForeignKey("review_task_id");

            // The field name *is* the identity: FieldReview derives from Entity<string> keyed on
            // it, and the aggregate refuses duplicate names within a task. So the key is
            // (task, field name) rather than a surrogate, and FieldName is ignored because it is a
            // computed alias for Id with no setter for EF to write through.
            field.Property(f => f.Id).HasColumnName("field_name").HasMaxLength(100);
            field.HasKey("review_task_id", nameof(FieldReview.Id));
            field.Ignore(f => f.FieldName);
            field.Ignore(f => f.IsResolved);
            field.Property(f => f.SuggestedValue).HasColumnName("suggested_value").HasMaxLength(2000);
            field.Property(f => f.AcceptedValue).HasColumnName("accepted_value").HasMaxLength(2000);
            field.Property(f => f.Confidence).HasColumnName("confidence").HasPrecision(3, 2);
            field.Property(f => f.IsMandatory).HasColumnName("is_mandatory");
            field.Property(f => f.CitationPage).HasColumnName("citation_page");
            field.Property(f => f.CitationSnippet).HasColumnName("citation_snippet").HasMaxLength(500);
            field.Property(f => f.ScoringNote).HasColumnName("scoring_note").HasMaxLength(1000);
            field.Property(f => f.ReviewerNote).HasColumnName("reviewer_note").HasMaxLength(1000);
            field.Property(f => f.WasCorrected).HasColumnName("was_corrected");
            field.Property(f => f.ResolvedBy).HasColumnName("resolved_by").HasMaxLength(256);
            field.Property(f => f.ResolvedAt).HasColumnName("resolved_at");
        });

        // The queue is filtered by status on every load, and cancelling on supersession looks a
        // task up by document.
        builder.HasIndex(t => t.Status).HasDatabaseName("ix_review_tasks_status");
        builder.HasIndex(t => t.DocumentId).HasDatabaseName("ix_review_tasks_document");

        builder.Ignore(t => t.DomainEvents);
        builder.Ignore(t => t.UnresolvedMandatoryFields);
    }
}
