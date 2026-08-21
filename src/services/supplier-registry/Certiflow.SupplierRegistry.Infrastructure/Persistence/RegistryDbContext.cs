using Certiflow.Persistence;
using Certiflow.SupplierRegistry.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Certiflow.SupplierRegistry.Infrastructure.Persistence;

public sealed class RegistryDbContext(DbContextOptions<RegistryDbContext> options)
    : DbContext(options), IOutboxContext
{
    public const string Schema = "registry";

    public DbSet<Supplier> Suppliers => Set<Supplier>();

    public DbSet<ComplianceProfile> Profiles => Set<ComplianceProfile>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new SupplierConfiguration());
        modelBuilder.ApplyConfiguration(new ComplianceProfileConfiguration());
        modelBuilder.AddMessagingTables();
    }
}

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("suppliers");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("supplier_id")
            .HasConversion(id => id.Value, value => new SupplierId(value))
            .ValueGeneratedNever();

        builder.Property(s => s.CategoryId)
            .HasColumnName("category_id")
            .HasConversion(
                id => id.HasValue ? id.Value.Value : (Guid?)null,
                value => value.HasValue ? new CategoryId(value.Value) : null);

        builder.Property(s => s.LegalName).HasColumnName("legal_name").HasMaxLength(200).IsRequired();
        builder.Property(s => s.TradingName).HasColumnName("trading_name").HasMaxLength(200);
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);

        builder.OwnsOne(s => s.RegistrationNumber, number =>
        {
            // Both forms are stored: Normalized is what uniqueness is checked against, Value is
            // what the supplier actually wrote and what a human should see.
            number.Property(n => n.Value).HasColumnName("registration_number").HasMaxLength(64).IsRequired();
            number.Property(n => n.Normalized).HasColumnName("registration_number_normalized").HasMaxLength(64).IsRequired();
        });

        builder.OwnsOne(s => s.Country, country =>
            country.Property(c => c.Value).HasColumnName("country_code").HasMaxLength(2).IsRequired());

        builder.OwnsMany(s => s.Contacts, contact =>
        {
            contact.ToTable("supplier_contacts");
            contact.WithOwner().HasForeignKey("supplier_id");

            contact.Property(c => c.Id).HasColumnName("contact_id").ValueGeneratedNever();
            contact.HasKey("supplier_id", nameof(SupplierContact.Id));

            contact.Property(c => c.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            contact.Property(c => c.Role).HasColumnName("role").HasMaxLength(100);
            contact.Property(c => c.IsPrimary).HasColumnName("is_primary");

            contact.OwnsOne(c => c.Email, email =>
                email.Property(e => e.Value).HasColumnName("email").HasMaxLength(256).IsRequired());
        });

        builder.Ignore(s => s.DomainEvents);
        builder.Ignore(s => s.PrimaryContact);
    }
}

internal sealed class ComplianceProfileConfiguration : IEntityTypeConfiguration<ComplianceProfile>
{
    public void Configure(EntityTypeBuilder<ComplianceProfile> builder)
    {
        builder.ToTable("compliance_profiles");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("category_id")
            .HasConversion(id => id.Value, value => new CategoryId(value))
            .ValueGeneratedNever();

        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(p => p.PublishedVersion).HasColumnName("published_version");
        builder.Property(p => p.HasUnpublishedChanges).HasColumnName("has_unpublished_changes");
        builder.Property(p => p.PublishedAt).HasColumnName("published_at");

        builder.OwnsMany(p => p.Requirements, requirement =>
        {
            requirement.ToTable("requirements");
            requirement.WithOwner().HasForeignKey("category_id");

            requirement.Property(r => r.Id)
                .HasColumnName("requirement_id")
                .HasConversion(id => id.Value, value => new RequirementId(value))
                .ValueGeneratedNever();

            requirement.HasKey("category_id", nameof(Requirement.Id));

            requirement.Property(r => r.IsMandatory).HasColumnName("is_mandatory");
            requirement.Property(r => r.RenewalLeadTimeDays).HasColumnName("renewal_lead_time_days");
            requirement.Property(r => r.MinValidityDays).HasColumnName("min_validity_days");
            requirement.Property(r => r.RequiresIssuerMatch).HasColumnName("requires_issuer_match");
            requirement.Property(r => r.AutoAcceptThreshold).HasColumnName("auto_accept_threshold").HasPrecision(3, 2);
            requirement.Property(r => r.IsDeprecated).HasColumnName("is_deprecated");

            requirement.OwnsOne(r => r.DocumentType, type =>
                type.Property(t => t.Value).HasColumnName("document_type").HasMaxLength(100).IsRequired());

            // Accepted issuers are a list of strings read only as a whole, so they go to one column
            // rather than a table nothing would ever join to.
            requirement.PrimitiveCollection(r => r.AcceptedIssuers).HasColumnName("accepted_issuers");
        });

        builder.Ignore(p => p.ActiveRequirements);
        builder.Ignore(p => p.DomainEvents);
    }
}
