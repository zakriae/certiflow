# ADR-0006 — A report reads through to its sources, not around them

**Status:** accepted · **Date:** 2026-08-21

## Context

BC6 Reporting produces a supplier compliance certificate: the supplier's identity, the requirements
they carry, the evidence satisfying each one, and an overall status — with a verification hash over
the whole set (FR-6.1). A buyer forwards this document to an auditor.

None of those facts belong to BC6. Identity is BC1's, obligations and evidence are BC5's.

Every other cross-context read in Certiflow uses an event-fed local copy. BC3 keeps a
`RegistryReadModel` of suppliers and requirements so it can check an issuer without asking BC1
anything. That is the normal answer in an event-driven system and it is the right one nearly
everywhere: no runtime coupling, no cascading failure, no shared uptime.

## Decision

**Reporting calls Compliance and Supplier Registry synchronously, at generation time.**

`HttpComplianceSnapshotSource` fetches the supplier, their compliance position and the category name
over HTTP, assembles a `SupplierComplianceSnapshot`, and the fingerprint is computed over that.

## Why the usual answer is wrong here

A read model is eventually consistent, and "eventually" is unbounded. A report generated from one
is an assertion about a moment that has already passed by an unknown amount.

That is tolerable for a dashboard, where the reader knows they are looking at a live system and can
refresh. It is not tolerable for this document, because of what the document is: it carries a date,
a verification hash, and a claim of the form *this supplier was compliant*. Issuing that from a copy
that is a few seconds — or, after a consumer failure, a few hours — behind means attesting to facts
that may already be false. The hash makes it worse rather than better: it lends the stale claim an
air of having been checked.

The failure mode is also invisible in exactly the wrong way. A lagging read model produces a report
that looks completely normal. Nothing on the page says "these facts are from a copy that had not
caught up."

## What this costs, stated plainly

- **If Compliance or Registry is down, no report is produced.** BC6's availability is now the
  product of theirs.
- This is the only synchronous service-to-service call in the system, so it is the only place where
  that is true.

Both are accepted, because the alternative failure is worse. Refusing to issue an attestation you
cannot substantiate is correct behaviour; issuing a confident, hash-stamped, wrong one is not.

Generation is asynchronous (FR-6.4), so the cost lands as a job in `Failed` with a readable reason —
`"compliance has no record at /api/suppliers/{id}/compliance"` — rather than a 500 at the caller.

## What keeps the blast radius small

- **Ten-second timeouts** on both clients, not the 100-second default. A wedged dependency produces
  a recorded failure while someone still cares, instead of a job that looks alive and is not.
- **Failures are caught and recorded on the aggregate**, never rethrown. Letting them escape would
  dead-letter the message and strand the job in `Generating` forever.
- **The category name degrades rather than fails.** It is presentational; a missing profile falls
  back to the id rather than failing a report whose compliance facts are all present.
- **Concurrency capped at 4.** Rendering is CPU-bound and each report makes three HTTP calls; an
  unbounded consumer would turn a burst of requests into a dependency outage it caused itself.

## Consequences

- Reporting has no read model and consumes no events for its data. It has an inbox only for its own
  `ReportRequested`.
- The fingerprint is computed over the snapshot before rendering, so it attests to the facts and not
  to the bytes of a layout. Restyle the PDF and the hash is unchanged; alter a certificate number
  and it is not.
- `GET /api/reports/{id}/verify` recomputes the fingerprint from the supplier's position *now*. A
  mismatch does not mean tampering — it means the supplier's compliance has changed since the report
  was issued, which is precisely what someone holding a three-month-old PDF needs to be told.
- If a portfolio report over hundreds of suppliers is ever built (FR-6.2, a **Should** that is not
  built), this decision has to be revisited: hundreds of synchronous calls per report is a different
  problem, and the answer there is probably a batch endpoint on BC5 rather than a read model.
