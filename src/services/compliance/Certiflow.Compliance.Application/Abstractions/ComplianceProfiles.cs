using Certiflow.Compliance.Domain;

namespace Certiflow.Compliance.Application.Abstractions;

/// <summary>
/// One requirement as this service received it from the Supplier Registry. The Application-layer
/// shape, deliberately separate from both BC1's published contract and BC5's domain
/// <see cref="RequirementSpecification"/> — Infrastructure maps the first into this, and this
/// into the third.
/// </summary>
public sealed record RequirementDefinition(
    Guid RequirementId,
    string DocumentType,
    bool IsMandatory,
    int RenewalLeadTimeDays,
    int MinValidityDays)
{
    public RequirementSpecification ToSpecification() => new(
        new RequirementId(RequirementId),
        DocumentType,
        IsMandatory,
        RenewalLeadTimeDays,
        MinValidityDays);
}

/// <summary>The latest published profile version for a category, as this service last saw it.</summary>
public sealed record ProfileVersionSnapshot(
    Guid CategoryId,
    int ProfileVersion,
    IReadOnlyList<RequirementDefinition> Requirements);

/// <summary>
/// Stores the most recent profile version per category.
/// <para>
/// This exists to close an ordering gap that would otherwise be invisible until a demo. A supplier
/// registered <em>after</em> its category's profile was published would receive no obligations at
/// all — the profile event has already been and gone — and would sit on the dashboard as vacuously
/// Pending forever. Keeping the last published version lets registration apply it immediately, so
/// the two events can arrive in either order and the result is the same.
/// </para>
/// <para>
/// It is a read model owned by BC5, not a query into BC1 (SRS §4.3, Published Language).
/// </para>
/// </summary>
public interface IComplianceProfileStore
{
    Task<ProfileVersionSnapshot?> FindLatestAsync(Guid categoryId, CancellationToken cancellationToken);

    Task SaveAsync(ProfileVersionSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when an operation arrives for a supplier this service has never heard of — normally
/// because <c>SupplierRegistered</c> has not been processed yet.
/// <para>
/// Deliberately an exception rather than a silent no-op: the message should go back on the queue
/// and be retried, because the registration is almost certainly moments behind it. Swallowing it
/// would lose the submission permanently and leave an obligation stuck as Missing.
/// </para>
/// </summary>
/// <summary>
/// Deliberately <b>not</b> marked <c>IResourceNotFound</c>. Consumers throw this to put a message
/// back on the queue when <c>SupplierRegistered</c> has not been processed yet, so it is usually a
/// timing signal rather than an answer to a question a user asked. Mapping it to 404 would be right
/// for the read endpoint and quietly wrong everywhere else; the read endpoint returns its own 404.
/// </summary>
public sealed class SupplierComplianceStateNotFoundException(SupplierId supplierId)
    : Exception($"No compliance state for supplier {supplierId}. It may not have been registered yet.")
{
    public SupplierId SupplierId { get; } = supplierId;
}
