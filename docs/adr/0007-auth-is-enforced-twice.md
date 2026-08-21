# ADR-0007 — Authentication is enforced at the gateway *and* in every service

**Status:** accepted · **Date:** 2026-08-21

## Context

SRS §20 R4 decided the auth approach up front: build against a **seeded JWT issuer** with the four
app roles, and swap it for Entra External ID later. The token shape does not change, so nothing
downstream should have to.

NFR-7 requires role-based authorisation enforced server-side. NFR-8 requires the tenant guard in the
gateway **and** each service.

## The seeded issuer signs with RS256 and publishes a JWKS

The shorter option was an HMAC secret shared by the gateway and the services. It was rejected for
two reasons, and the second matters more:

1. A shared secret means the same secret in eight config files, which is what NFR-9 exists to
   prevent.
2. It would make the services' validation code **different from what Entra needs**. With a symmetric
   key, the swap to Entra is a rewrite of every service's auth setup. With RS256 and a JWKS, every
   service already does exactly what it will do in production: point `Authority` at an issuer, fetch
   `/.well-known/openid-configuration`, fetch the signing keys, validate. Swapping issuers is a
   config value.

The gateway generates its key pair at startup and holds it in memory, so restarting it invalidates
every issued token. That is correct for a stand-in: it keeps the demo issuer from quietly becoming
something anybody depends on.

## Enforcement in both places

The gateway is the public front door. It validates the token, applies a coarse-grained policy per
route, and forwards the token unchanged.

Every service then validates the token again and applies its own policies.

This is not belt-and-braces theatre. Before it existed:

```
curl http://localhost:5290/api/review-tasks    →  200, the entire review queue
```

Every service listened on a port and the gateway was a suggestion. In Container Apps the services
sit behind internal ingress, which is a real control — but "the network protects it" is precisely
the assumption that turns one misconfigured ingress rule into total data access.

The services use a **fallback policy** requiring an authenticated user, so a new endpoint is
protected unless it says `AllowAnonymous`. Forgetting fails closed. `/health` is the deliberate
exception: a container probe has no token.

## Service-to-service calls

Reporting reads Compliance and Registry to build a certificate (ADR-0006), and those calls now need
an identity. Two options were rejected:

- **Forward the requester's token.** Generation is asynchronous, so the token would have to be
  stored in a queued message and replayed minutes later. Persisting a user's JWT is a poor idea.
- **A client secret.** It would put a secret in a config file to model something that, in Azure,
  involves no secret at all.

Instead Reporting presents a **workload identity**: a token carrying the `Service` role, obtained
from the issuer, attached by a `DelegatingHandler` and refreshed a minute before expiry.

In Azure this class is replaced by `DefaultAzureCredential` and a scope, because the managed
identity issues the token and nothing has to ask. **The fact that no credential is presented to
obtain it is the point of managed identity**, and the seeded endpoint models that deliberately — it
is `Development`-only and compiled out of anything else.

## Roles

`Admin`, `Reviewer`, `Auditor`, `SupplierUser` — the strings Entra will emit as app roles.

`Auditor` reads everything and writes nothing (FR-8.6). `SupplierUser` is **absent from every policy
it was not explicitly added to**, so a supplier reaching a portfolio-wide list is a compile-time
omission rather than a runtime leak (NFR-8). The one write a supplier may perform is uploading a
document — which is also the one path that must never be anonymous, because it is what spends tokens
at Azure OpenAI (guardrail G1).

Requesting a report is deliberately allowed for `Auditor`. It creates a row, so it is technically a
write, but what it produces is a rendering of facts the auditor may already read — and an auditor
who cannot pull a compliance certificate cannot do the job the role exists for.

## Consequences

- Two places to change when a policy changes. Accepted; the alternative is a single point of
  bypass.
- The first authenticated request to a cold service pays for OIDC discovery (observed: a few
  seconds locally). Every request after it is unaffected.
- `MapInboundClaims` is off everywhere. On, .NET rewrites `sub` and `email` into WS-Federation URIs
  and the claims stop matching the token — `/auth/me` returned nulls for fields plainly present in
  the JWT until this was found.
