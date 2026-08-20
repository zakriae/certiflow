using System.Text.Json;
using System.Text.Json.Serialization;
using Certiflow.Compliance.Domain;
using Certiflow.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.Compliance.Infrastructure.Persistence;

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options)
    : DbContext(options), IOutboxContext
{
    public const string Schema = "compliance";

    public DbSet<SupplierComplianceState> SupplierCompliance => Set<SupplierComplianceState>();

    public DbSet<ProfileVersionRecord> ProfileVersions => Set<ProfileVersionRecord>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new SupplierComplianceStateConfiguration());
        modelBuilder.ApplyConfiguration(new ProfileVersionRecordConfiguration());
        modelBuilder.AddMessagingTables();
    }
}

/// <summary>
/// The last published profile version for a category, as this service saw it.
/// <para>
/// Kept so a supplier registered *after* its category's profile was published still gets its
/// obligations — the event has already been and gone, and without this the supplier would sit on
/// the dashboard as vacuously Pending forever.
/// </para>
/// </summary>
public sealed class ProfileVersionRecord
{
    private ProfileVersionRecord() => RequirementsJson = null!;

    public ProfileVersionRecord(Guid categoryId, int profileVersion, string requirementsJson)
    {
        CategoryId = categoryId;
        ProfileVersion = profileVersion;
        RequirementsJson = requirementsJson;
    }

    public Guid CategoryId { get; private set; }

    public int ProfileVersion { get; private set; }

    public string RequirementsJson { get; private set; }

    public void Update(int profileVersion, string requirementsJson)
    {
        ProfileVersion = profileVersion;
        RequirementsJson = requirementsJson;
    }
}

/// <summary>Serialiser for the value objects stored as JSON inside an obligation row.</summary>
public static class ComplianceJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed class SupplierComplianceStateConfiguration : IEntityTypeConfiguration<SupplierComplianceState>
{
    public void Configure(EntityTypeBuilder<SupplierComplianceState> builder)
    {
        builder.ToTable("supplier_compliance");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("supplier_id")
            .HasConversion(id => id.Value, value => new SupplierId(value))
            .ValueGeneratedNever();

        builder.Property(s => s.CategoryId).HasColumnName("category_id");
        builder.Property(s => s.ProfileVersion).HasColumnName("profile_version");
        builder.Property(s => s.LastEvaluatedAt).HasColumnName("last_evaluated_at");

        // OverallStatus is derived, never stored as the source of truth (ADR-0001). It is written
        // to a column purely so the dashboard can filter and count in SQL inside NFR-2's 500 ms
        // budget - and it is never read back into the aggregate, which recomputes it every time.
        builder.Ignore(s => s.OverallStatus);
        builder.Property<string>("overall_status").HasMaxLength(20);

        builder.OwnsMany(s => s.Obligations, obligation =>
        {
            obligation.ToTable("obligations");
            obligation.WithOwner().HasForeignKey("supplier_id");

            obligation.Property(o => o.Id)
                .HasColumnName("requirement_id")
                .HasConversion(id => id.Value, value => new RequirementId(value));

            obligation.HasKey("supplier_id", nameof(Obligation.Id));

            obligation.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
            obligation.Property(o => o.IsApplicable).HasColumnName("is_applicable");

            obligation.Property(o => o.PendingDocumentId)
                .HasColumnName("pending_document_id")
                .HasConversion(
                    id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new DocumentId(value.Value) : null);

            // The value objects go to JSON rather than owned types. CertificateEvidence's
            // constructor takes a ValidityPeriod, and EF refuses to bind a navigation to a
            // constructor parameter - the same wall BC3's ExtractedField hit. System.Text.Json can
            // rebuild them through their real constructors, so the validation survives the round
            // trip and the domain keeps its guarantees.
            obligation.Property(o => o.Specification)
                .HasColumnName("specification_json")
                .HasColumnType("nvarchar(max)")
                .HasConversion(
                    spec => JsonSerializer.Serialize(spec, ComplianceJson.Options),
                    json => JsonSerializer.Deserialize<RequirementSpecification>(json, ComplianceJson.Options)!);

            obligation.Property(o => o.CurrentEvidence)
                .HasColumnName("current_evidence_json")
                .HasColumnType("nvarchar(max)")
                .HasConversion(
                    evidence => evidence == null ? null : JsonSerializer.Serialize(evidence, ComplianceJson.Options),
                    json => json == null ? null : JsonSerializer.Deserialize<CertificateEvidence>(json, ComplianceJson.Options));

            obligation.Property<List<RetiredEvidence>>("_history")
                .HasColumnName("history_json")
                .HasColumnType("nvarchar(max)")
                .HasConversion(
                    history => JsonSerializer.Serialize(history, ComplianceJson.Options),
                    json => JsonSerializer.Deserialize<List<RetiredEvidence>>(json, ComplianceJson.Options)!,
                    new ValueComparer<List<RetiredEvidence>>(
                        (left, right) => left!.SequenceEqual(right!),
                        history => history.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                        history => history.ToList()));

            obligation.Ignore(o => o.History);
            obligation.Ignore(o => o.DocumentType);
            obligation.Ignore(o => o.IsMandatory);
        });

        builder.Ignore(s => s.DomainEvents);
    }
}

internal sealed class ProfileVersionRecordConfiguration : IEntityTypeConfiguration<ProfileVersionRecord>
{
    public void Configure(EntityTypeBuilder<ProfileVersionRecord> builder)
    {
        builder.ToTable("profile_versions");
        builder.HasKey(p => p.CategoryId);

        builder.Property(p => p.CategoryId).HasColumnName("category_id").ValueGeneratedNever();
        builder.Property(p => p.ProfileVersion).HasColumnName("profile_version");
        builder.Property(p => p.RequirementsJson).HasColumnName("requirements_json").HasColumnType("nvarchar(max)").IsRequired();
    }
}
