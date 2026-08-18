# ADR-0004 — What may be shared between eight bounded contexts

**Status:** Accepted · **Date:** 2026-08-18 · **Supports:** SRS §19 Q1, Q2

## Context

Eight contexts that all model suppliers, documents and dates will converge on a shared model unless
something actively prevents it — usually by way of a helpful `Certiflow.Common` that starts with a
base entity class and ends up holding `Supplier`. At that point the split is decorative: the services
deploy separately but cannot change independently, which is the worst of both designs.

## Decision

Exactly two assemblies are shared, with different rules.

**`Certiflow.SharedKernel`** — base types and technical primitives only: `Entity<TId>`,
`AggregateRoot<TId>`, `IDomainEvent`, `Guard`, `DomainRuleViolationException`, `IClock`. It has **zero
NuGet dependencies**, because every Domain project references it and anything added here lands inside
six domains at once. An architecture test asserts it contains no type whose name mentions a business
concept — Supplier, Document, Certificate, Compliance, Requirement, Obligation, Extraction, Review,
Verdict, Audit, Evidence, Validity.

**`Certiflow.Contracts`** — integration event DTOs only. It references **nothing at all**, not even
the shared kernel: it is consumed by every service and, in a real deployment, published as a package,
so every dependency it carries it imposes on nine consumers. Its types use primitives and strings
rather than enums or value objects, so a consumer is never coupled to a publisher's model.

**Domain projects may not reference `Certiflow.Contracts`.** Integration events are the Published
Language *between* services, translated at the Infrastructure boundary. A domain that speaks its own
wire format is a domain shaped by it, and every context's model starts drifting toward the others'.
This is why each context defines its own `SupplierId`, its own `DocumentId`, and its own
`DocumentApproved` — the names are identical on both sides of the boundary, the types are not.

Everything else is duplicated on purpose. BC5's `RequirementSpecification` is its own translation of
BC1's `RequirementDescriptor`. The duplication is what lets BC1 change its model without breaking
compliance evaluation.

## Consequences

**Good.** Contexts can be changed, tested and reasoned about one at a time. Domain projects have no
NuGet dependencies at all, so every domain test is a pure function test. The rules are enforced by
`tests/Certiflow.ArchitectureTests` and fail the build, so they hold on a Friday afternoon too.

**Bad.** Genuine duplication: five contexts define a `SupplierId` wrapping a `Guid`, and a change to
the published shape of a requirement is edited in two places. That is the price of the boundary, and
it is a price paid in typing rather than in coupling.

**Cost.** Translating between domain events and integration events is real code in every
Infrastructure layer — mapping that a shared model would not need. Mapperly generates most of it.

## Alternatives rejected

- **A shared `Certiflow.Domain` with common entities.** The failure mode described in Context.
- **Domains reference `Certiflow.Contracts` directly.** Removes the translation layer and roughly a
  third of the duplication, at the cost of every aggregate being shaped by its wire format. Reasonable
  for a modular monolith; wrong once contexts version independently.
- **No shared kernel at all.** Six copies of `Entity<TId>`. Purer, and worse: base types carry no
  business meaning, so sharing them couples nothing that matters.
