using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Certiflow.ArchitectureTests;

/// <summary>
/// SRS §5.2 and NFR-18 — the dependency rule, enforced by the build.
/// <para>
/// This file is the answer to <i>"how do you keep Clean Architecture from rotting?"</i>, which is a
/// question real clients ask. Code review catches a violation if the reviewer is paying attention on
/// a Friday afternoon; a failing test catches it every time. The whole suite costs one NuGet package.
/// </para>
/// </summary>
public sealed class DependencyRuleTests
{
    /// <summary>
    /// The bounded contexts, by name. Only the name travels through <see cref="MemberDataAttribute"/> —
    /// an <see cref="Assembly"/> in a <see cref="TheoryData{T}"/> is not serialisable and xUnit's
    /// analyser rejects it, which this build treats as an error.
    /// </summary>
    private static readonly string[] Contexts =
    [
        "SupplierRegistry",
        "Intake",
        "Intelligence",
        "Verification",
        "Compliance",
        "Audit",
        "Reporting",
    ];

    public static TheoryData<string> BoundedContexts() => [.. Contexts];

    /// <summary>
    /// Resolved from a type in each assembly rather than by name string, so deleting or renaming a
    /// context breaks this file at compile time instead of silently dropping it out of the checks.
    /// </summary>
    private static Assembly AssemblyFor(string context) => context switch
    {
        "SupplierRegistry" => typeof(SupplierRegistry.Domain.Supplier).Assembly,
        "Intake" => typeof(Intake.Domain.Document).Assembly,
        "Intelligence" => typeof(Intelligence.Domain.ExtractionJob).Assembly,
        "Verification" => typeof(Verification.Domain.ReviewTask).Assembly,
        "Compliance" => typeof(Compliance.Domain.SupplierComplianceState).Assembly,
        "Audit" => typeof(Audit.Domain.AuditEntry).Assembly,
        "Reporting" => typeof(Reporting.Domain.Report).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(context), context, "Unknown bounded context."),
    };

    private static string NamespaceFor(string context) => $"Certiflow.{context}.Domain";

    /// <summary>
    /// Namespace prefixes no Domain assembly may reference. Persistence, messaging, HTTP, mediation
    /// and serialisation all belong outside the domain — if a rule needs any of them, the rule is in
    /// the wrong layer.
    /// </summary>
    private static readonly string[] ForbiddenInDomain =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.Logging",
        "MediatR",
        "MassTransit",
        "Azure",
        "FluentValidation",
        "Newtonsoft.Json",
        "System.Text.Json",
        "System.Net.Http",
        "System.Data",
        "UglyToad",
        "QuestPDF",
    ];

    [Theory]
    [MemberData(nameof(BoundedContexts))]
    public void Domain_does_not_depend_on_infrastructure_concerns(string context)
    {
        var result = Types.InAssembly(AssemblyFor(context))
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenInDomain)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0}.Domain must stay free of infrastructure, but these types reference it: {1}",
            context,
            FailingTypes(result));
    }

    [Theory]
    [MemberData(nameof(BoundedContexts))]
    public void Domain_does_not_depend_on_any_other_bounded_context(string context)
    {
        // The rule that keeps eight contexts from quietly becoming one. Contexts talk through the
        // integration events in Certiflow.Contracts; a direct reference would make a shared model of
        // the two and defeat the whole split (SRS §4.3).
        var otherContexts = Contexts
            .Where(name => !string.Equals(name, context, StringComparison.Ordinal))
            .Select(NamespaceFor)
            .ToArray();

        var result = Types.InAssembly(AssemblyFor(context))
            .ShouldNot()
            .HaveDependencyOnAny(otherContexts)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0}.Domain must not reference another context's domain, but these types do: {1}",
            context,
            FailingTypes(result));
    }

    [Theory]
    [MemberData(nameof(BoundedContexts))]
    public void Domain_does_not_depend_on_the_integration_event_contracts(string context)
    {
        // Certiflow.Contracts is the Published Language *between* services, translated at the
        // Infrastructure boundary. A domain that speaks it directly is a domain shaped by its wire
        // format, and every context's model starts drifting toward the others' (SRS §3, §12).
        var result = Types.InAssembly(AssemblyFor(context))
            .ShouldNot()
            .HaveDependencyOn("Certiflow.Contracts")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0}.Domain must not reference Certiflow.Contracts, but these types do: {1}",
            context,
            FailingTypes(result));
    }

    [Theory]
    [MemberData(nameof(BoundedContexts))]
    public void Domain_events_are_sealed(string context)
    {
        // Asserting on a raised event is a one-liner because they are records with structural
        // equality, and an extensible event is a lie about what happened.
        var result = Types.InAssembly(AssemblyFor(context))
            .That()
            .ImplementInterface(typeof(SharedKernel.IDomainEvent))
            .And()
            .AreNotAbstract()
            .Should()
            .BeSealed()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0} domain events must be sealed, but these are not: {1}",
            context,
            FailingTypes(result));
    }

    /// <summary>
    /// Application assemblies, by name. Grows as each context gets its use cases; the rules below
    /// then apply to it automatically.
    /// </summary>
    private static readonly string[] ApplicationContexts = ["Compliance", "Reporting"];

    public static TheoryData<string> ApplicationLayers() => [.. ApplicationContexts];

    private static Assembly ApplicationAssemblyFor(string context) => context switch
    {
        "Compliance" => typeof(Compliance.Application.DependencyInjection).Assembly,
        "Reporting" => typeof(Reporting.Application.Generation.RequestReportCommand).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(context), context, "Unknown application layer."),
    };

    [Theory]
    [MemberData(nameof(ApplicationLayers))]
    public void Application_does_not_depend_on_the_integration_event_contracts(string context)
    {
        // ADR-0004. Integration events are the Published Language *between* services, translated at
        // the Infrastructure boundary. An Application layer that took them as command parameters
        // would be shaped by another service's wire format, and a change to BC1's contract would
        // reach into BC5's use cases.
        var result = Types.InAssembly(ApplicationAssemblyFor(context))
            .ShouldNot()
            .HaveDependencyOn("Certiflow.Contracts")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0}.Application must not reference Certiflow.Contracts, but these types do: {1}",
            context,
            FailingTypes(result));
    }

    [Theory]
    [MemberData(nameof(ApplicationLayers))]
    public void Application_does_not_depend_on_persistence_or_transport(string context)
    {
        // Application may use MediatR, FluentValidation and DI abstractions — it may not know how
        // anything is stored or delivered. The ports it declares are the seam, and the moment a
        // handler references EF Core or MassTransit directly, that seam is gone.
        string[] forbidden =
        [
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "MassTransit",
            "Azure",
            "System.Data",
            "Microsoft.Data",
        ];

        var result = Types.InAssembly(ApplicationAssemblyFor(context))
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0}.Application must stay free of persistence and transport, but these types reference it: {1}",
            context,
            FailingTypes(result));
    }

    [Fact]
    public void The_shared_kernel_contains_no_business_concepts()
    {
        // Referenced by every Domain project, so anything added here lands inside six domains at
        // once. It may hold base types and primitives; the moment it holds a Supplier or a
        // Certificate, the contexts share a model whether they meant to or not (SRS §5.3).
        var businessWords = new[]
        {
            "Supplier", "Document", "Certificate", "Compliance", "Requirement", "Obligation",
            "Extraction", "Review", "Verdict", "Audit", "Evidence", "Validity",
        };

        var offenders = typeof(SharedKernel.Entity<>).Assembly
            .GetTypes()
            .Where(t => businessWords.Any(word => t.Name.Contains(word, StringComparison.Ordinal)))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "Certiflow.SharedKernel must hold no business concepts, but found: {0}",
            string.Join(", ", offenders));
    }

    [Fact]
    public void The_shared_persistence_plumbing_contains_no_business_concepts()
    {
        // Certiflow.Persistence may reference EF Core - it is Infrastructure, not domain. What it
        // may never hold is a business concept. Sharing "how a message is stored" couples nothing
        // that matters; sharing "what a Supplier is" would make eight services one (ADR-0004).
        string[] businessWords =
        [
            "Supplier", "Document", "Certificate", "Compliance", "Requirement", "Obligation",
            "Extraction", "Review", "Verdict", "Audit", "Evidence", "Validity",
        ];

        var offenders = typeof(Persistence.OutboxMessage).Assembly
            .GetTypes()
            .Where(t => businessWords.Any(word => t.Name.Contains(word, StringComparison.Ordinal)))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "Certiflow.Persistence must stay plumbing, but found: {0}", string.Join(", ", offenders));
    }

    [Fact]
    public void The_shared_persistence_plumbing_does_not_reference_any_bounded_context()
    {
        // It is referenced by every service, so a dependency here is a dependency everywhere.
        var referenced = typeof(Persistence.OutboxMessage).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Certiflow.", StringComparison.Ordinal))
            .ToList();

        referenced.Should().BeEmpty(
            "Certiflow.Persistence must not depend on any context, but references: {0}",
            string.Join(", ", referenced));
    }

    [Fact]
    public void The_integration_contracts_reference_nothing_of_ours()
    {
        // Not even the shared kernel (SRS §5.3). This assembly is consumed by every service and, in a
        // real deployment, published as a package — every dependency it carries is a dependency it
        // imposes on nine consumers.
        var referenced = typeof(Contracts.IIntegrationEvent).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Certiflow", StringComparison.Ordinal))
            .ToList();

        referenced.Should().BeEmpty(
            "Certiflow.Contracts must be standalone, but references: {0}",
            string.Join(", ", referenced));
    }

    private static string FailingTypes(TestResult result) =>
        result.FailingTypeNames is null ? "(none reported)" : string.Join(", ", result.FailingTypeNames);
}
