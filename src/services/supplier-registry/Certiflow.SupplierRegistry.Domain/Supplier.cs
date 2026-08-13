using Certiflow.SharedKernel;
using Certiflow.SupplierRegistry.Domain.Events;

namespace Certiflow.SupplierRegistry.Domain;

public enum SupplierStatus
{
    /// <summary>Being set up. Generates no obligations and no notifications.</summary>
    Draft = 1,

    Active = 2,

    /// <summary>Temporarily out of use. Stops generating notifications (FR-1.8).</summary>
    Suspended = 3,

    /// <summary>Relationship ended. Terminal — history is retained, nothing more is expected.</summary>
    Offboarded = 4,
}

/// <summary>A named person at a supplier. An entity inside the <see cref="Supplier"/> aggregate.</summary>
public sealed class SupplierContact : Entity<Guid>
{
    internal SupplierContact(Guid id, string name, EmailAddress email, string? role, bool isPrimary)
        : base(id)
    {
        Name = Guard.AgainstNullOrWhiteSpace(name, "registry.contact.name_required");
        Email = Guard.AgainstNull(email, "registry.contact.email_required");
        Role = role;
        IsPrimary = isPrimary;
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private SupplierContact()
    {
        Name = null!;
        Email = null!;
    }

    public string Name { get; private set; }

    public EmailAddress Email { get; private set; }

    public string? Role { get; private set; }

    /// <summary>
    /// The one contact who receives compliance correspondence. Exactly one while the supplier is
    /// Active, because "we emailed someone there" is not a defence at audit time.
    /// </summary>
    public bool IsPrimary { get; private set; }

    internal void MakePrimary() => IsPrimary = true;

    internal void Demote() => IsPrimary = false;
}

/// <summary>
/// An external organisation required to evidence compliance (SRS §3, §6.1).
/// <para>
/// A supporting aggregate, not core — but it owns the two facts everything downstream depends on:
/// which category a supplier belongs to (and therefore what is required of it) and who to contact
/// when something lapses.
/// </para>
/// </summary>
public sealed class Supplier : AggregateRoot<SupplierId>
{
    private readonly List<SupplierContact> _contacts = [];

    private Supplier(
        SupplierId id,
        string legalName,
        string? tradingName,
        RegistrationNumber registrationNumber,
        CountryCode country,
        CategoryId? categoryId)
        : base(id)
    {
        LegalName = legalName;
        TradingName = tradingName;
        RegistrationNumber = registrationNumber;
        Country = country;
        CategoryId = categoryId;
        Status = SupplierStatus.Draft;
    }

    /// <summary>Required by EF Core; never called by domain code.</summary>
    private Supplier()
    {
        LegalName = null!;
        RegistrationNumber = null!;
        Country = null!;
    }

    public string LegalName { get; private set; }

    /// <summary>
    /// The name the supplier trades under, when it differs. Not cosmetic: BC3's entity-match check
    /// accepts a certificate issued to either name (SRS §8.3), and without this a legitimate
    /// certificate in the trading name would be flagged as belonging to a different company.
    /// </summary>
    public string? TradingName { get; private set; }

    public RegistrationNumber RegistrationNumber { get; private set; }

    public CountryCode Country { get; private set; }

    /// <summary>Null while Draft. Determines which Requirements apply.</summary>
    public CategoryId? CategoryId { get; private set; }

    public SupplierStatus Status { get; private set; }

    public IReadOnlyList<SupplierContact> Contacts => _contacts.AsReadOnly();

    public SupplierContact? PrimaryContact => _contacts.SingleOrDefault(c => c.IsPrimary);

    /// <summary>
    /// A suspended or offboarded supplier is not chased (FR-1.8). Its compliance state is still
    /// derived and still visible — it simply stops generating email.
    /// </summary>
    public bool ShouldBeNotified => Status == SupplierStatus.Active;

    public static Supplier Register(
        string legalName,
        string? tradingName,
        RegistrationNumber registrationNumber,
        CountryCode country,
        CategoryId? categoryId = null)
    {
        var safeLegalName = Guard.AgainstNullOrWhiteSpace(legalName, "registry.supplier.legal_name_required");
        Guard.AgainstTooLong(safeLegalName, 200, "registry.supplier.legal_name_too_long");

        var supplier = new Supplier(
            SupplierId.New(),
            safeLegalName,
            string.IsNullOrWhiteSpace(tradingName) ? null : tradingName.Trim(),
            Guard.AgainstNull(registrationNumber, "registry.supplier.registration_number_required"),
            Guard.AgainstNull(country, "registry.supplier.country_required"),
            categoryId);

        supplier.Raise(new SupplierRegistered(
            supplier.Id, safeLegalName, supplier.TradingName, categoryId, country.Value));

        return supplier;
    }

    /// <summary>
    /// Adds a contact. The first contact added becomes primary automatically — requiring a separate
    /// call to promote the only contact there is would be a step that exists purely to be forgotten.
    /// </summary>
    public SupplierContact AddContact(string name, EmailAddress email, string? role = null, bool isPrimary = false)
    {
        EnsureNotOffboarded();

        Guard.Against(
            _contacts.Any(c => c.Email == email),
            "registry.supplier.duplicate_contact_email",
            $"{email} is already a contact for this supplier.");

        var makePrimary = isPrimary || _contacts.Count == 0;

        if (makePrimary)
        {
            foreach (var existing in _contacts)
            {
                existing.Demote();
            }
        }

        var contact = new SupplierContact(Guid.CreateVersion7(), name, email, role, makePrimary);
        _contacts.Add(contact);

        return contact;
    }

    public void SetPrimaryContact(Guid contactId)
    {
        EnsureNotOffboarded();

        var contact = _contacts.SingleOrDefault(c => c.Id == contactId)
            ?? throw new DomainRuleViolationException(
                "registry.supplier.unknown_contact",
                $"Supplier {Id} has no contact {contactId}.");

        foreach (var existing in _contacts)
        {
            existing.Demote();
        }

        contact.MakePrimary();
    }

    public void AssignCategory(CategoryId categoryId)
    {
        EnsureNotOffboarded();

        if (CategoryId == categoryId)
        {
            return;
        }

        var previous = CategoryId;
        CategoryId = categoryId;

        // Only announced for an Active supplier: a Draft supplier's category changing several times
        // during setup is not news, and BC5 has nothing to rebuild until the supplier goes live.
        if (Status == SupplierStatus.Active && previous is { } previousCategory)
        {
            Raise(new SupplierCategoryChanged(Id, previousCategory, categoryId));
        }
    }

    /// <summary>
    /// Activates the supplier, which is what makes it start counting toward compliance.
    /// <para>
    /// Both preconditions of SRS §6.1 are enforced here. They are not bureaucracy: a supplier with no
    /// category has no requirements and would sit on the dashboard as vacuously compliant, and a
    /// supplier with no primary contact cannot be told that anything has lapsed.
    /// </para>
    /// </summary>
    public void Activate()
    {
        EnsureNotOffboarded();

        if (Status == SupplierStatus.Active)
        {
            return;
        }

        var categoryId = CategoryId ?? throw new DomainRuleViolationException(
            "registry.supplier.activation_requires_category",
            $"Supplier {Id} cannot be activated without a category — it would have no requirements.");

        Guard.Require(
            PrimaryContact is not null,
            "registry.supplier.activation_requires_primary_contact",
            $"Supplier {Id} cannot be activated without a primary contact — nobody could be told when something lapses.");

        Status = SupplierStatus.Active;

        Raise(new SupplierActivated(Id, categoryId));
    }

    public void Suspend(string reason)
    {
        EnsureNotOffboarded();

        var safeReason = Guard.AgainstNullOrWhiteSpace(reason, "registry.supplier.suspension_reason_required");

        if (Status == SupplierStatus.Suspended)
        {
            return;
        }

        Status = SupplierStatus.Suspended;

        Raise(new SupplierSuspended(Id, safeReason));
    }

    public void Reinstate()
    {
        EnsureNotOffboarded();

        Guard.Require(
            Status == SupplierStatus.Suspended,
            "registry.supplier.not_suspended",
            $"Supplier {Id} is {Status} and cannot be reinstated.");

        Status = SupplierStatus.Draft;
        Activate();
    }

    /// <summary>
    /// Ends the relationship. Terminal: an offboarded supplier is never reactivated, because doing so
    /// would resurrect obligations against evidence that may be years stale. A returning supplier is
    /// registered afresh.
    /// </summary>
    public void Offboard(string reason)
    {
        var safeReason = Guard.AgainstNullOrWhiteSpace(reason, "registry.supplier.offboard_reason_required");

        if (Status == SupplierStatus.Offboarded)
        {
            return;
        }

        Status = SupplierStatus.Offboarded;

        Raise(new SupplierOffboarded(Id, safeReason));
    }

    private void EnsureNotOffboarded() =>
        Guard.Against(
            Status == SupplierStatus.Offboarded,
            "registry.supplier.offboarded",
            $"Supplier {Id} has been offboarded. Register a new supplier instead.");
}
