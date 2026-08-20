using Certiflow.Intake.Domain;
using Certiflow.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.Intake.Infrastructure.Persistence;

/// <summary>
/// Document Intake's own database context.
/// <para>
/// Everything lives in the <c>intake</c> schema. One SQL instance serves all eight contexts because
/// eight Azure SQL databases would cost $40–80/month against a $20 ceiling, but each context keeps
/// its own schema, its own <c>DbContext</c>, its own migrations and its own SQL login, and
/// cross-schema queries and foreign keys are forbidden. Everything that matters about the
/// separation is preserved; only the physical split is deferred, and that is a connection string
/// (SRS §13.1, §19 Q7).
/// </para>
/// </summary>
public sealed class IntakeDbContext(DbContextOptions<IntakeDbContext> options) : DbContext(options), IOutboxContext
{
    public const string Schema = "intake";

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new DocumentConfiguration());
        modelBuilder.AddMessagingTables();
    }
}

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(d => d.Id);

        // Strongly-typed ids cost nothing at the database: a value converter maps them straight to
        // uniqueidentifier, and a method taking (SupplierId, RequirementId) cannot be called with
        // the arguments the wrong way round.
        builder.Property(d => d.Id)
            .HasColumnName("document_id")
            .HasConversion(id => id.Value, value => new DocumentId(value))
            .ValueGeneratedNever();

        builder.Property(d => d.SupplierId)
            .HasColumnName("supplier_id")
            .HasConversion(id => id.Value, value => new SupplierId(value));

        // Nullable: a document can be uploaded before it is bound to a requirement, so the
        // converter has to survive a null on the way in and produce one on the way out.
        builder.Property(d => d.RequirementId)
            .HasColumnName("requirement_id")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new RequirementId(value.Value) : null);

        builder.Property(d => d.SupersedesDocumentId)
            .HasColumnName("supersedes_document_id")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new DocumentId(value.Value) : null);

        builder.Property(d => d.SupersededByDocumentId)
            .HasColumnName("superseded_by_document_id")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new DocumentId(value.Value) : null);

        builder.Property(d => d.ExpectedDocumentType).HasColumnName("expected_document_type").HasMaxLength(100);
        builder.Property(d => d.FileName).HasColumnName("file_name").HasMaxLength(260);
        builder.Property(d => d.ContentType).HasColumnName("content_type").HasMaxLength(100);
        builder.Property(d => d.SizeBytes).HasColumnName("size_bytes");
        builder.Property(d => d.PageCount).HasColumnName("page_count");
        builder.Property(d => d.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(256);
        builder.Property(d => d.UploadedAt).HasColumnName("uploaded_at");
        builder.Property(d => d.QuarantineReason).HasColumnName("quarantine_reason").HasMaxLength(500);

        builder.Property(d => d.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);

        // Value objects map into the parent table as owned types - they have no identity of their
        // own, so giving them tables would invent one.
        builder.OwnsOne(d => d.Sha256, hash =>
            hash.Property(h => h.Value).HasColumnName("sha256").HasMaxLength(64).IsRequired());

        builder.OwnsOne(d => d.StorageReference, reference =>
        {
            reference.Property(r => r.Container).HasColumnName("storage_container").HasMaxLength(63).IsRequired();
            reference.Property(r => r.BlobPath).HasColumnName("storage_blob_path").HasMaxLength(1024).IsRequired();
        });

        // The duplicate check of FR-2.4 runs on every upload, so it gets an index rather than a
        // table scan that grows with the corpus.
        builder.HasIndex(d => new { d.SupplierId, d.RequirementId }).HasDatabaseName("ix_documents_supplier_requirement");

        builder.Ignore(d => d.DomainEvents);
    }
}
