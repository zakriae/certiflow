using Certiflow.Compliance.Application.Abstractions;
using Certiflow.Compliance.Application.Evidence;
using Certiflow.Compliance.Application.Suppliers;
using Certiflow.Compliance.Domain;
using FluentAssertions;
using Xunit;

using static Certiflow.Compliance.Application.Tests.Fixture;

namespace Certiflow.Compliance.Application.Tests;

/// <summary>
/// Regression tests for a race found by running the system, not by reading it.
/// <para>
/// <c>SupplierRegistered</c> and <c>ComplianceProfileVersionPublished</c> travel as separate
/// messages on separate queues and are consumed concurrently. Registration applies the stored
/// profile snapshot if one exists; publication applies the profile to every supplier it can see.
/// Each order works alone — but interleaved, registration sees no snapshot yet <em>and</em>
/// publication's listing does not yet see the new supplier. The supplier ends up with no
/// obligations at all and reads as vacuously Pending, silently, forever.
/// </para>
/// </summary>
public sealed class ProfileReconciliationTests
{
    private readonly InMemoryComplianceRepository _repository = new();

    private readonly InMemoryProfileStore _profiles = new();

    private readonly CountingUnitOfWork _unitOfWork = new();

    private readonly FixedClock _clock = new(Now);

    private ComplianceStateLoader Loader => new(_repository, _profiles, _clock);

    /// <summary>Registers a supplier while the profile store is still empty — the losing interleave.</summary>
    private async Task RegisterWithNoProfileYetAsync()
    {
        var handler = new RegisterSupplierComplianceHandler(_repository, _profiles, _unitOfWork, _clock);

        await handler.Handle(new RegisterSupplierComplianceCommand(SupplierGuid, Logistics), CancellationToken.None);
    }

    private async Task PublishProfileAsync(int version = 1) =>
        await _profiles.SaveAsync(
            new ProfileVersionSnapshot(Logistics, version, [Iso9001()]), CancellationToken.None);

    [Fact]
    public async Task A_supplier_registered_before_the_profile_arrives_has_no_obligations()
    {
        // The bug as observed: not a crash, just a supplier with nothing to be compliant about.
        await RegisterWithNoProfileYetAsync();

        var state = _repository.All.Single();
        state.ProfileVersion.Should().Be(0);
        state.Obligations.Should().BeEmpty();
        state.OverallStatus.Should().Be(ComplianceStatus.Pending);
    }

    [Fact]
    public async Task Loading_the_state_afterwards_repairs_it()
    {
        await RegisterWithNoProfileYetAsync();
        await PublishProfileAsync();

        // No new message, no replay, no operator action - the next read reconciles.
        var state = await Loader.LoadAsync(Supplier, CancellationToken.None);

        state.ProfileVersion.Should().Be(1);
        state.Obligations.Should().ContainSingle();
        state.OverallStatus.Should().Be(ComplianceStatus.NonCompliant, "the mandatory requirement is missing");
    }

    [Fact]
    public async Task A_submission_arriving_after_the_race_still_finds_its_obligation()
    {
        // The symptom that made the race visible: recording a submission threw
        // 'not_in_profile' because the obligation the document satisfies did not exist.
        await RegisterWithNoProfileYetAsync();
        await PublishProfileAsync();

        var handler = new RecordSubmissionHandler(Loader, _unitOfWork, _clock);

        var act = async () => await handler.Handle(
            new RecordSubmissionCommand(SupplierGuid, RequirementGuid, DocumentGuid), CancellationToken.None);

        await act.Should().NotThrowAsync();
        _repository.All.Single().FindObligation(Requirement)!.Status
            .Should().Be(ObligationStatus.AwaitingReview);
    }

    [Fact]
    public async Task Reconciling_is_a_no_op_when_nothing_is_stale()
    {
        await PublishProfileAsync();
        await RegisterWithNoProfileYetAsync();

        var state = await Loader.LoadAsync(Supplier, CancellationToken.None);
        state.ClearDomainEvents();

        await Loader.ReconcileProfileAsync(state, CancellationToken.None);

        // The aggregate ignores a version it already holds, so a healthy state costs nothing and
        // announces nothing.
        state.ProfileVersion.Should().Be(1);
        state.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconciling_never_moves_a_state_backwards()
    {
        await PublishProfileAsync(version: 5);
        await RegisterWithNoProfileYetAsync();

        // A late-arriving older snapshot must not roll the rules back.
        await _profiles.SaveAsync(
            new ProfileVersionSnapshot(Logistics, 2, [Iso9001(leadTimeDays: 5)]), CancellationToken.None);

        var state = await Loader.LoadAsync(Supplier, CancellationToken.None);

        state.ProfileVersion.Should().Be(5);
    }
}
