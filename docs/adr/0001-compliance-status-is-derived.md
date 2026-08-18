# ADR-0001 — Compliance status is derived, never stored

**Status:** Accepted · **Date:** 2026-08-18 · **Answers:** SRS §19 Q12

## Context

A supplier is Compliant, At Risk, Non-Compliant or Pending. The obvious implementation is a `status`
column, written whenever something happens: a document is approved, a certificate expires, a profile
changes.

That column is wrong within a day. A certificate expiring at midnight changes nothing in the database
until some job notices, so the dashboard shows green for a supplier who is not compliant. Every
mechanism that keeps such a column correct — triggers, nightly sweeps, event handlers on six
different events — is a mechanism that can fail silently, and the failure looks exactly like
"everything is fine".

## Decision

**Status is a function, not a field.**

`Obligation.StatusOn(DateOnly today)` is pure: given the evidence held and the requirement's
thresholds, it returns the status for that date. `SupplierComplianceState.OverallStatusOn(today)` is
the worst status across mandatory, still-applicable obligations — the enum is ordered best-to-worst
so this is literally `Max()`.

No API accepts a status. No method assigns one. The only way a status changes is that the evidence
changed or the date did.

A per-obligation `Status` snapshot **is** persisted, but only so list and dashboard queries can filter
in SQL within the 500 ms budget of NFR-2. It is refreshed by evaluation, never assigned from outside
the aggregate, and `StatusOn` remains the authority. The snapshot is a cache of the function, and the
tests assert that the two agree.

## Consequences

**Good.** Drift is structurally impossible: a test asserts that a supplier reads as Non-Compliant on
a date after expiry with no job having run and no row having been updated. The nightly Expiry Watch
becomes a pure event-emitter rather than a correctness-critical writer — if it fails to run, nothing
is *wrong*, only unannounced. Point-in-time queries (FR-5.8) come almost free, because "status as at
a past date" is the same function with a different argument.

**Bad.** Every status read needs a date, which makes signatures noisier. The persisted snapshot is a
second representation and could disagree with the function if a mutator forgot to refresh — mitigated
by routing every mutation through one `EmitTransitions` method and testing it.

**Cost.** Sorting a supplier list by *live* status cannot be done in SQL against the function; it
sorts against the snapshot. Acceptable at portfolio scale, and the honest answer at real scale is a
materialised read model rebuilt from events, which this design already permits.

## Alternatives rejected

- **Stored status with event handlers.** The default. Rejected because correctness then depends on
  every handler being registered and every event being delivered — and the failure is invisible.
- **Database computed column.** Puts the most valuable business rule in the schema, where it cannot
  be unit-tested and does not survive a change of database.
