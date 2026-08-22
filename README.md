# Certiflow

Supplier compliance and document verification. A buyer collects certificates from suppliers — ISO
9001, insurance, trade licences — and has to prove, on demand, that every supplier was compliant on
a given date and that nobody edited the record afterwards.

Built as a portfolio project: .NET 9, DDD, event-driven microservices, Angular 22, Azure.

## The part worth reading first

Three decisions carry the whole system, and each has an ADR.

**Confidence is computed, not reported** ([ADR-0002](docs/adr/0002-computed-confidence.md)). The
model is never asked how sure it is. Every extracted field must cite a page and a verbatim snippet,
and that snippet is verified against the parsed text before anything is trusted. A citation that
cannot be located scores **zero** — grounding is a veto, not a weight. The remaining signals are
deterministic checks: does the date parse, does the holder match the supplier on record, is the
issuer accepted. Confidence is their weighted result, rounded toward zero.

**Compliance status is derived, never stored**
([ADR-0001](docs/adr/0001-compliance-status-is-derived.md)). A supplier's status is computed from
approved evidence and the published profile at the moment you ask. There is a status column, but it
is a cache for SQL queries and is only ever written by the derivation that produces it — so it
cannot drift into disagreeing with the truth.

**The audit trail is hash-chained** ([ADR-0003](docs/adr/0003-hash-chained-audit-trail.md)). Every
integration event becomes an append-only entry whose hash covers its predecessor. `verify-chain`
recomputes the whole chain and names the first broken entry. A development-only endpoint edits one
row in raw SQL so the detection can be demonstrated rather than described.

## Running it

See [docs/local-development.md](docs/local-development.md). Short version:

```bash
bash scripts/run-all.sh
```

Then http://localhost:4200 — the sign-in screen prints the demo accounts.

## Architecture

Eight bounded contexts, a YARP gateway, and an Angular SPA.

| Context | Owns |
|---|---|
| Supplier Registry | Suppliers, categories, compliance profiles |
| Document Intake | Upload, hashing, blob storage, duplicate detection |
| Document Intelligence | LLM extraction, grounding verification, confidence |
| Verification | The review queue, approval and rejection |
| Compliance | Obligations, evidence, derived status, the expiry sweep |
| Reporting | The compliance certificate PDF and its verification hash |
| Notification | Renewal reminders and the in-app inbox |
| Audit Trail | The hash-chained ledger |

They share one database with a schema each, and one shared kernel that holds no business concept
([ADR-0004](docs/adr/0004-shared-kernel-and-contracts.md)). Services communicate only through
integration events, with one deliberate exception recorded in
[ADR-0006](docs/adr/0006-reports-read-through-not-around.md).

Authentication runs at the gateway **and** in every service
([ADR-0007](docs/adr/0007-auth-is-enforced-twice.md)), against a seeded OIDC issuer that Entra
External ID replaces by changing a config value.

## Tests

```bash
dotnet test
```

379 tests. The architecture tests are the interesting ones: they enforce the dependency rule across
every context, so a Domain layer that grows a reference to Infrastructure fails the build rather
than a review.

## What is not built

Portfolio reports, evidence-pack ZIPs, admin alerting, FR/EN templates, digests, Excel export and
OCR fallback. All are **Should** or **Could** in the SRS. The scope cut was depth, not architecture.
