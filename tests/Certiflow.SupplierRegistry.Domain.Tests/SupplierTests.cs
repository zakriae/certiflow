using Certiflow.SharedKernel;
using Certiflow.SupplierRegistry.Domain.Events;
using FluentAssertions;
using Xunit;

namespace Certiflow.SupplierRegistry.Domain.Tests;

public sealed class SupplierTests
{
    private static readonly CategoryId Logistics = CategoryId.New();

    private static Supplier Draft(CategoryId? category = null) => Supplier.Register(
        legalName: "Meridian Logistics SARL",
        tradingName: "Meridian Freight",
        registrationNumber: RegistrationNumber.Parse("FR 812 345 678"),
        country: CountryCode.Parse("fr"),
        categoryId: category);

    private static Supplier Active()
    {
        var supplier = Draft(Logistics);
        supplier.AddContact("Amine Berrada", EmailAddress.Parse("amine@meridian-logistics.demo"));
        supplier.Activate();
        supplier.ClearDomainEvents();
        return supplier;
    }

    [Fact]
    public void A_registered_supplier_starts_as_a_draft()
    {
        var supplier = Draft();

        supplier.Status.Should().Be(SupplierStatus.Draft);
        supplier.DomainEvents.OfType<SupplierRegistered>().Should().ContainSingle();
    }

    [Fact]
    public void Activation_requires_a_category()
    {
        // A supplier with no category has no requirements, and would sit on the dashboard as
        // vacuously compliant.
        var supplier = Draft(category: null);
        supplier.AddContact("Amine Berrada", EmailAddress.Parse("amine@meridian-logistics.demo"));

        var act = supplier.Activate;

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.supplier.activation_requires_category");
    }

    [Fact]
    public void Activation_requires_a_primary_contact()
    {
        // Otherwise nobody can be told when something lapses, which is most of what the system does.
        var supplier = Draft(Logistics);

        var act = supplier.Activate;

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.supplier.activation_requires_primary_contact");
    }

    [Fact]
    public void The_first_contact_added_becomes_primary_automatically()
    {
        // A separate call to promote the only contact there is would be a step that exists purely
        // to be forgotten.
        var supplier = Draft(Logistics);

        var contact = supplier.AddContact("Amine Berrada", EmailAddress.Parse("amine@meridian-logistics.demo"));

        contact.IsPrimary.Should().BeTrue();
        supplier.PrimaryContact.Should().Be(contact);
    }

    [Fact]
    public void There_is_never_more_than_one_primary_contact()
    {
        var supplier = Draft(Logistics);
        supplier.AddContact("Amine Berrada", EmailAddress.Parse("amine@meridian-logistics.demo"));

        supplier.AddContact("Claire Dubois", EmailAddress.Parse("claire@meridian-logistics.demo"), isPrimary: true);

        supplier.Contacts.Should().HaveCount(2);
        supplier.Contacts.Count(c => c.IsPrimary).Should().Be(1);
        supplier.PrimaryContact!.Name.Should().Be("Claire Dubois");
    }

    [Fact]
    public void The_same_email_cannot_be_added_twice()
    {
        var supplier = Draft(Logistics);
        supplier.AddContact("Amine Berrada", EmailAddress.Parse("amine@meridian-logistics.demo"));

        var act = () => supplier.AddContact("A. Berrada", EmailAddress.Parse("AMINE@Meridian-Logistics.Demo"));

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.supplier.duplicate_contact_email");
    }

    [Fact]
    public void Activating_announces_the_category_that_now_applies()
    {
        var supplier = Draft(Logistics);
        supplier.AddContact("Amine Berrada", EmailAddress.Parse("amine@meridian-logistics.demo"));

        supplier.Activate();

        supplier.Status.Should().Be(SupplierStatus.Active);
        supplier.DomainEvents.OfType<SupplierActivated>().Should().ContainSingle()
            .Which.CategoryId.Should().Be(Logistics);
    }

    [Fact]
    public void Activating_an_already_active_supplier_is_harmless()
    {
        var supplier = Active();

        supplier.Activate();

        supplier.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Changing_the_category_of_an_active_supplier_announces_it()
    {
        // BC5 has to rebuild the supplier's obligations from the new category's profile.
        var supplier = Active();
        var facilities = CategoryId.New();

        supplier.AssignCategory(facilities);

        supplier.DomainEvents.OfType<SupplierCategoryChanged>().Should().ContainSingle()
            .Which.Should().Match<SupplierCategoryChanged>(e =>
                e.PreviousCategoryId == Logistics && e.NewCategoryId == facilities);
    }

    [Fact]
    public void Changing_the_category_of_a_draft_supplier_is_not_news()
    {
        // BC5 has nothing to rebuild until the supplier goes live.
        var supplier = Draft(Logistics);
        supplier.ClearDomainEvents();

        supplier.AssignCategory(CategoryId.New());

        supplier.DomainEvents.OfType<SupplierCategoryChanged>().Should().BeEmpty();
    }

    [Fact]
    public void A_suspended_supplier_stops_being_notified_but_keeps_its_record()
    {
        // FR-1.8. Its compliance state is still derived and still visible.
        var supplier = Active();

        supplier.Suspend("Contract paused pending renegotiation.");

        supplier.Status.Should().Be(SupplierStatus.Suspended);
        supplier.ShouldBeNotified.Should().BeFalse();
        supplier.DomainEvents.OfType<SupplierSuspended>().Should().ContainSingle();
    }

    [Fact]
    public void A_suspended_supplier_can_be_reinstated()
    {
        var supplier = Active();
        supplier.Suspend("Paused.");
        supplier.ClearDomainEvents();

        supplier.Reinstate();

        supplier.Status.Should().Be(SupplierStatus.Active);
        supplier.DomainEvents.OfType<SupplierActivated>().Should().ContainSingle();
    }

    [Fact]
    public void Offboarding_is_terminal()
    {
        // Reactivating would resurrect obligations against evidence that may be years stale. A
        // returning supplier is registered afresh.
        var supplier = Active();
        supplier.Offboard("Contract ended.");

        var act = supplier.Activate;

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.supplier.offboarded");
    }

    [Fact]
    public void An_offboarded_supplier_accepts_no_further_changes()
    {
        var supplier = Active();
        supplier.Offboard("Contract ended.");

        var act = () => supplier.AddContact("New Person", EmailAddress.Parse("new@meridian-logistics.demo"));

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("registry.supplier.offboarded");
    }
}
