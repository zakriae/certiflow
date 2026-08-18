# ADR-0003 — The audit trail is append-only and hash-chained

**Status:** Accepted · **Date:** 2026-08-18 · **Answers:** SRS §19 Q8

## Context

The product's claim is that a compliance decision can be proven after the fact. An audit table that
anyone with database access can `UPDATE` proves nothing — and "we don't do that" is not something an
auditor can verify.

## Decision

Every integration event from every context is persisted as an `AuditEntry`, and each entry hashes its
own contents **together with its predecessor's hash**:

```
EntryHash = SHA256( len:EntryId ␟ len:OccurredAt ␟ len:Actor ␟ len:Action ␟
                    len:EntityType ␟ len:EntityId ␟ len:CorrelationId ␟
                    len:PayloadJson ␟ len:PreviousHash ␟ )
```

Editing any row invalidates that row's hash and every hash after it. `AuditChainVerifier` recomputes
the chain and reports the **first** break — a list of five hundred consequent failures tells an
auditor nothing that "entry 412 was altered" does not tell them better.

Four things are checked, because they fail differently:

| Check | Catches |
|---|---|
| Entry hashes to its stored value | An `UPDATE` to any column |
| Entry ids are consecutive | A `DELETE` — the survivors still link to each other correctly |
| `PreviousHash` matches the predecessor | An `INSERT`, or a spliced replacement |
| Entry 1 references the genesis hash | Rewriting the start of the chain |

**Append-only is enforced by the type, not by discipline.** Every property is get-only, there is no
mutating method, and the only constructor is private. `FromPersistedState` exists for EF
materialisation — and, deliberately, for the tamper test, which needs to build an entry whose stored
hash disagrees with its contents, because that is exactly what a tampered row is.

**Fields are length-prefixed in the canonical form.** Without prefixes, an actor of `"a␟b"` with
action `"c"` hashes identically to actor `"a"` with action `"b␟c"` — enough freedom for a determined
editor to rewrite a record and keep its hash valid.

Timestamps are normalised to UTC and round-trip formatted, so a chain verifies identically wherever it
is read.

## Consequences

**Good.** Tampering becomes detectable and *locatable*, which is a far stronger claim than
"restricted access". It demos in ten seconds: edit one row in SQL, run verify-chain, watch it name
the row — an abstract guarantee made visible (SRS §11.3).

**Bad.** `EntryId` is derived from the predecessor, so appends must be serialised. BC8 consumes its
subscription with a single concurrent handler, backstopped by a unique index so a second writer loses
on insert rather than forking the chain. This caps audit write throughput at one writer — fine here,
and the honest answer at real scale is a database sequence plus periodic checkpoint hashes.

**Bad.** Verifying a long chain is O(n) and must read every entry in order. Mitigated by verifying
slices; the verifier accepts any ascending range and only applies the genesis rule when the slice
starts at entry 1.

**Not claimed.** This does not make tampering *impossible*. Someone with write access can rewrite the
entire chain from the altered row forward. Defending against that needs an external anchor — periodic
publication of the head hash, or the storage-level immutability policy of FR-8.8, which is a Could.
The honest claim is: undetected tampering requires rewriting every subsequent entry, and that is a
much higher bar than editing one row.

## Alternatives rejected

- **Database temporal tables / CDC.** Good for history, useless as proof: the same privileges that
  edit the table edit its history.
- **Sign each entry with a private key.** Stronger, but the signing key has to live somewhere the
  application can reach, which reintroduces the problem one layer down. Worth revisiting with Key
  Vault-managed keys if the requirement ever hardens.
