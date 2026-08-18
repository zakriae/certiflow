using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Application.Suppliers;
using Certiflow.Compliance.Domain;
using FluentAssertions;
using Xunit;

using static Certiflow.Compliance.Application.Tests.Fixture;

namespace Certiflow.Compliance.Application.Tests;

/// <summary>
/// The two events that shape a supplier's obligations — <c>SupplierRegistered</c> and
/// <c>ComplianceProfileVersionPublished</c> — arrive over a bus with no ordering guarantee. These
/// tests exist to prove the result is the same whichever order they land in, because the failure
/// otherwise is silent: a supplier with no obligations that reads as Pending forever.
/// </summary>
public sealed class RegistrationAndProfileOrderingTests
{
    private readonly InMemoryComplianceRepository _repository = new();

    private readonly InMemoryProfileStore _profiles = new();

    private readonly CountingUnitOfWork _unitOfWork = new();

    private readonly FixedClock _clock = new(Now);

    private RegisterSupplierComplianceHandler Register =>
        new(_repository, _profiles, _unitOfWork, _clock);

    private ApplyProfileVersionHandler ApplyProfile =>
        new(_repository, _profiles, _unitOfWork, _clock);

    private static RegisterSupplierComplianceCommand Registration => new(SupplierGuid, Logistics);

    private static ApplyProfileVersionCommand Profile(int version = 1, int leadTimeDays = 30) =>
        new(Logistics, version, [Iso9001(leadTimeDays)]);

    [Fact]
    public async Task Registration_then_profile_gives_the_supplier_its_obligations()
    {
        await Register.Handle(Registration, CancellationToken.None);
        await ApplyProfile.Handle(Profile(), CancellationToken.None);

        var state = _repository.All.Single();
        state.Obligations.Should().ContainSingle();
        state.OverallStatus.Should().Be(ComplianceStatus.NonCompliant, "the mandatory requirement is missing");
    }

    [Fact]
    public async Task Profile_then_registration_gives_exactly_the_same_result()
    {
        // The event this supplier needed has already been and gone. Without the stored snapshot it
        // would sit on the dashboard as vacuously Pending, with no obligations, forever.
        await ApplyProfile.Handle(Profile(), CancellationToken.None);
        await Register.Handle(Registration, CancellationToken.None);

        var state = _repository.All.Single();
        state.Obligations.Should().ContainSingle();
        state.ProfileVersion.Should().Be(1);
        state.OverallStatus.Should().Be(ComplianceStatus.NonCompliant);
    }

    [Fact]
    public async Task A_supplier_registered_before_any_profile_exists_is_pending_not_compliant()
    {
        await Register.Handle(Registration, CancellationToken.None);

        var state = _repository.All.Single();
        state.ProfileVersion.Should().Be(0);
        state.OverallStatus.Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public async Task Registering_the_same_supplier_twice_is_a_no_op()
    {
        await Register.Handle(Registration, CancellationToken.None);
        await ApplyProfile.Handle(Profile(), CancellationToken.None);

        await Register.Handle(Registration, CancellationToken.None);

        _repository.All.Should().ContainSingle();
        _repository.All.Single().ProfileVersion.Should().Be(1, "the redelivery must not reset the state");
    }

    [Fact]
    public async Task Publishing_a_new_version_rebuilds_obligations_for_every_supplier_in_the_category()
    {
        await Register.Handle(Registration, CancellationToken.None);
        await ApplyProfile.Handle(Profile(version: 1, leadTimeDays: 30), CancellationToken.None);

        await ApplyProfile.Handle(Profile(version: 2, leadTimeDays: 180), CancellationToken.None);

        var state = _repository.All.Single();
        state.ProfileVersion.Should().Be(2);
        state.FindObligation(Requirement)!.Specification.RenewalLeadTimeDays.Should().Be(180);
    }

    [Fact]
    public async Task An_out_of_order_older_profile_version_is_ignored()
    {
        await Register.Handle(Registration, CancellationToken.None);
        await ApplyProfile.Handle(Profile(version: 5, leadTimeDays: 90), CancellationToken.None);

        await ApplyProfile.Handle(Profile(version: 2, leadTimeDays: 10), CancellationToken.None);

        var state = _repository.All.Single();
        state.ProfileVersion.Should().Be(5, "applying an older version would silently roll the rules back");
        state.FindObligation(Requirement)!.Specification.RenewalLeadTimeDays.Should().Be(90);
    }

    [Fact]
    public async Task Publishing_a_profile_for_a_category_with_no_suppliers_still_stores_the_snapshot()
    {
        await ApplyProfile.Handle(Profile(), CancellationToken.None);

        var stored = await _profiles.FindLatestAsync(Logistics, CancellationToken.None);

        stored.Should().NotBeNull();
        stored!.ProfileVersion.Should().Be(1);
    }
}
