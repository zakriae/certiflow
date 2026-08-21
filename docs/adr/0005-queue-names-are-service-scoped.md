# ADR-0005 — Every service's queue names are scoped to that service

**Status:** accepted · **Date:** 2026-08-21

## Context

MassTransit names a consumer's receive endpoint after the message it consumes. Under the default
kebab-case formatter, a consumer of `DocumentStored` gets a queue called `document-stored` —
regardless of which service declares it.

Certiflow has eight bounded contexts and several of them consume the same events. Document
Intelligence extracts from `DocumentStored`; Verification raises a review task from it; Compliance
marks the obligation as submitted; Audit records that it happened. Four services, one default queue
name.

Two services declaring the same queue name are not two independent subscribers. They are two
**competing consumers on one queue**, and the broker delivers each message to exactly one of them.

## The failure this caused

The audit service was written with the default formatter and collided with five other queues:
`document-stored`, `document-approved`, `document-rejected`, `document-superseded`,
`extraction-completed`, `supplier-registered`.

The symptom was not an error. Nothing logged a warning, no message dead-lettered, and
`verify-chain` reported a perfectly valid chain — of the wrong events. Roughly half of each event
type went to the business service and half to Audit, depending on which consumer the broker picked.
The ledger recorded `ComplianceStatusChanged` and `ReviewTaskRaised` faithfully while silently
missing `DocumentStored` and `DocumentApproved` — the two entries that carry a human being's name,
and the entire reason the audit trail exists.

It was found by running the full chain end to end and reading the ledger, not by any test. A chain
that verifies is not the same as a chain that is complete, and no amount of hash verification can
detect an event that never arrived.

## Decision

Every service sets an endpoint name formatter with a **service-specific prefix**:

```csharp
bus.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(prefix: "audit", includeNamespace: false));
```

Audit's queues are `audit-document-stored`, `audit-document-approved`, and so on. Each service owns
a namespace no other service can enter.

## Why a prefix rather than named endpoints

The alternative is naming each receive endpoint explicitly, which is what the services written
before Audit did — `document-stored-compliance`, `document-stored-verification`. That works, and it
is how the collisions that already existed had been avoided.

It relies on someone remembering. Audit has 21 consumers; the 22nd would have been added by copying
the 21st, and if the 21st had been the one written without a suffix the bug would have returned. A
prefix set once at the bus applies to every consumer that exists and every consumer that will.

## Consequences

- A new consumer in any service cannot collide with another service by construction.
- Queue names are longer and carry their owner, which makes the RabbitMQ management UI legible:
  `rabbitmqctl list_queues` now shows who is listening to what.
- Renaming a service's prefix orphans its queues. In development `scripts/reset-local.sh` purges the
  broker; in Azure, an orphaned Service Bus subscription accrues no cost but must be deleted by hand.
- **The general lesson, which outlives this fix:** an event-driven system fails silently when
  delivery is wrong but well-formed. The guard is not a unit test — it is running the whole chain
  and checking that what arrived is what was sent.
