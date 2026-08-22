# Running Certiflow locally

Until the .NET Aspire host lands, the backing services run as plain containers. The service code is
identical either way — Aspire injects connection strings rather than changing how anything works.

## Prerequisites

- .NET 9 SDK (pinned in `global.json`)
- Docker Desktop, running
- `az login`, for the keyless Azure OpenAI credential

## Backing services

```bash
docker run -d --name certiflow-sql \
  -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Certiflow!Local1' -e MSSQL_PID=Developer \
  -p 11433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

```bash
docker run -d --name certiflow-azurite \
  -p 10000:10000 -p 10001:10001 -p 10002:10002 \
  mcr.microsoft.com/azure-storage/azurite:latest \
  azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0 --skipApiVersionCheck
```

`--skipApiVersionCheck` is not optional. The Azure Blob SDK negotiates a service API version newer
than the Azurite emulator recognises, and without the flag every upload fails with
`InvalidHeaderValue`. The alternative — pinning the SDK down to an older service version — would
make local development diverge from Azure, which is the wrong trade for an emulator quirk.

Port 11433 rather than 1433 so a locally installed SQL Server does not collide with the container.

```bash
docker run -d --name certiflow-rabbit -p 5672:5672 -p 15672:15672 rabbitmq:3-management-alpine
```

RabbitMQ locally, Azure Service Bus when deployed. MassTransit makes the consumer code identical,
which is most of why it is here. The split exists because the Service Bus emulator cannot create
topics and subscriptions at runtime and MassTransit's topology does. The honest caveat: Service Bus
specifics — sessions, scheduled delivery, dead-letter semantics — are first exercised in Azure, not
on a laptop. Management UI at http://localhost:15672 (guest/guest).

## Running the services

Nine processes. Start order does not matter — each creates its own schema, and the compliance state
reconciles itself on read if the profile and the supplier arrive out of order (ADR-0005 covers the
related delivery hazard).

| Service | Port |
|---|---|
| Supplier Registry | 5270 |
| Document Intake | 5280 |
| Verification | 5290 |
| Compliance | 5300 |
| Audit Trail | 5310 |
| Reporting | 5320 |
| Notifications | 5330 |
| Gateway | 5000 |
| Intelligence worker | (no HTTP) |

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5280 dotnet run --project src/services/document-intake/Certiflow.Intake.Api
```

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project src/services/document-intelligence/Certiflow.Intelligence.Worker
```

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5290 dotnet run --project src/services/verification/Certiflow.Verification.Api
```

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5300 dotnet run --project src/services/compliance/Certiflow.Compliance.Api
```

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5310 dotnet run --project src/services/audit-trail/Certiflow.Audit.Api
```

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5270 dotnet run --project src/services/supplier-registry/Certiflow.SupplierRegistry.Api
```

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5320 dotnet run --project src/services/reporting/Certiflow.Reporting.Api
```

The worker needs `az login` — it calls Azure OpenAI with the keyless credential.

## The full chain

Compliance needs a supplier and a published profile before any document means anything. Until BC1
exists, the registry-sync endpoints stand in for its events — they call the same handlers the
consumers do, so the seeded path and the event path cannot diverge.

```bash
curl -X POST http://localhost:5300/api/registry-sync/profiles -H "Content-Type: application/json" -d '{"categoryId":"dddd1111-eeee-2222-ffff-333344445555","profileVersion":1,"requirements":[{"requirementId":"9999aaaa-8888-bbbb-7777-cccc66665555","documentType":"ISO 9001","isMandatory":true,"renewalLeadTimeDays":60,"minValidityDays":30}]}'
```

```bash
curl -X POST http://localhost:5300/api/registry-sync/suppliers -H "Content-Type: application/json" -d '{"supplierId":"aaaa1111-bbbb-2222-cccc-333344445555","categoryId":"dddd1111-eeee-2222-ffff-333344445555"}'
```

Then upload against that supplier and requirement, resolve the fields, approve, and watch the
status walk `NonCompliant → Pending → Compliant` on
`GET /api/suppliers/{id}/compliance`. Nothing touches a database directly at any point.

### Resetting between runs

```bash
bash scripts/reset-local.sh
```

Purges RabbitMQ **and** drops every schema. Purging the broker is not optional: dropping the
databases alone leaves the previous run's messages queued, and they redeliver against an empty
database and interleave with the new run's events. The symptom is obligations referencing
requirement ids that exist nowhere, and it has nothing to do with the code.

### Schema changes

The schema is built by **EF migrations**, in development and in Azure, from the same files. A
service applies any pending migrations for its own context on startup in development, so a fresh
clone needs no extra step — the logs name each migration as it runs.

Add or change a table, then:

```bash
dotnet ef migrations add DescribeTheChange --project src/services/<service>/<Service>.Infrastructure --startup-project src/services/<service>/<Service>.Api --context <Service>DbContext --output-dir Persistence/Migrations
```

Each context keeps its own `__migrations` history table **inside its own schema**, which is what
lets eight contexts share one database (SRS §13.1) — they never read each other's history.

This replaced a development-only bootstrap that created a schema which was missing and could not
evolve one that already existed. Adding a table to a live context did nothing at all, and the
failure surfaced only as `Invalid object name` the next time a consumer touched it. That cost real
time twice and needed an escape hatch that worked by dropping every table in the schema.

**Azure does not migrate on startup.** Container Apps runs several replicas, and several replicas
racing to apply the same migration is how a deployment corrupts a schema. Deployment runs them once,
as a step, before the new revision starts (NFR-19).

## Uploading a document

Generate the corpus first, then upload one of its certificates:

```bash
dotnet run --project src/tools/Certiflow.SeedCorpus -- --output /tmp/corpus
```

```bash
curl -X POST http://localhost:5280/api/documents -F "file=@/tmp/corpus/certificates/brightleaf-produce-co-iso-9001.pdf;type=application/pdf" -F "supplierId=aaaaaaaa-0000-0000-0000-000000000001" -F "requirementId=00000000-0000-0000-0000-000000000001" -F "documentType=ISO 9001"
```

`202 Accepted` with a document id. Then:

- `GET /api/documents/{id}` — the stored row, including hash, page count and storage reference
- `GET /api/_outbox` — the pending `DocumentStored` event, written in the same transaction

Re-uploading the identical file returns the original document id with `duplicateOfDocumentId` set,
and writes no second row, no second blob and no second outbox message (FR-2.4).

## Running the extraction spike

Needs `az login` and the Azure OpenAI resource. Calls a paid model, so it is a tool and never a test.

```bash
dotnet run --project src/tools/Certiflow.ExtractionSpike -- --take 3
```

```bash
dotnet run --project src/tools/Certiflow.ExtractionSpike -- --only Sterling
```

The second targets the deliberately mismatched certificate: it should score 0.80 and refuse to
auto-accept.

## Tearing down

```bash
docker rm -f certiflow-sql certiflow-azurite
```

## Running the front end

Node 22 is required (`.nvmrc`); the system Node is not touched.

```bash
cd src/web/certiflow-web
source ~/.nvm/nvm.sh && nvm use && npm install && npm start
```

`source ~/.nvm/nvm.sh` is not optional unless your shell profile already loads nvm — it is a shell
function, not a binary, so an unconfigured shell reports `command not found: nvm`. Sourcing it
affects that terminal only, which is the point: nvm's default alias is 22, and loading it from
`~/.zshrc` would switch every shell to Node 22 and shadow the system Node.

The proxy now has exactly two entries, both pointing at the gateway on 5000 — which is the gateway
earning its place. It used to list every service individually, and every new service meant another
line that existed only in development and had no counterpart in Azure.

### The screens

| Screen | Who sees it | What it is for |
|---|---|---|
| Dashboard | everyone | Portfolio counts, non-compliant list, all suppliers with filters (FR-5.3, FR-1.6) |
| Supplier | everyone | One supplier obligation by obligation, with the evidence behind each (FR-5.2) |
| Upload | supplier, reviewer, admin | Drag a PDF into extraction (FR-2.1) |
| Review queue | reviewer, admin | Document beside its fields, citations that navigate (FR-4.2) |
| Audit trail | auditor, reviewer, admin | The ledger, filters, and Verify chain (FR-8.3, FR-8.4) |
| Notifications | everyone | The in-app inbox, because mail is off (FR-7.4, FR-7.8) |
| Admin | admin | Register suppliers, publish compliance profiles (FR-1.1, FR-1.2, FR-1.3) |

Sign in at http://localhost:4200 with any of the demo accounts; the shared password is printed on
the screen and fetched from the gateway, so there is one place it is defined.

| Account | Role | Sees |
|---|---|---|
| `admin@certiflow.demo` | Admin | Everything, including publishing profiles |
| `reviewer@certiflow.demo` | Reviewer | Dashboard and the review queue |
| `auditor@certiflow.demo` | Auditor | Dashboard, audit trail, reports — no approvals (FR-8.6) |
| `supplier@certiflow.demo` | SupplierUser | Uploads only; refused every portfolio route (NFR-8) |

Restarting the gateway invalidates every issued token, because the seeded issuer generates its
signing key in memory. Signing in again is the fix, and that it is necessary is the point.

### Azurite needs CORS for the PDF viewer

Documents are served as short-lived SAS URLs (FR-2.5), so the browser fetches them straight from
storage. Azurite sends no CORS headers by default and the viewer fails with an opaque
"Failed to fetch":

```bash
az storage cors add --services b --methods GET HEAD OPTIONS --origins "http://localhost:4200" --allowed-headers "*" --exposed-headers "*" --max-age 3600 --connection-string "UseDevelopmentStorage=true"
```

Real Azure Storage needs the same rule for whatever origin serves the SPA. Streaming the bytes
through the API instead would dodge CORS entirely, but it would put every page of every document
through a container that scales on queue depth, and drop the guarantee that a document is only
reachable by a link that expires.

## The audit trail and the tamper test

Every service publishes into Audit, which appends one hash-chained entry per event (ADR-0003). The
ledger is the answer to "who did what, and can you prove it wasn't changed afterwards".

Drive one document through the whole chain first — publish a profile, register a supplier, upload,
resolve the fields, approve — then:

```bash
curl -s http://localhost:5310/api/audit
```

Ten entries for one certificate. Two of them carry a person's name: `DocumentStored` is the
uploader, `DocumentApproved` is the reviewer, and the segregation-of-duties rule guarantees they are
different people.

```bash
curl -s http://localhost:5310/api/audit/verify-chain
```

`isValid: true`, ten entries verified. Then break it — this endpoint runs a raw `UPDATE` against the
table, exactly as someone with database access would, and is compiled only in Development:

```bash
curl -s -X POST http://localhost:5310/api/audit/_tamper
```

```bash
curl -s http://localhost:5310/api/audit/verify-chain
```

Now `isValid: false`, `firstBrokenEntryId: 2`, `breakKind: ContentAltered`, and a detail line giving
the stored hash and the recomputed one. Verification stops at the first break: it reports one broken
entry rather than a cascade, because every entry after a tampered one has a wrong predecessor hash
and listing them all would bury the row that actually changed.

`GET /api/audit/2` shows the row as it stands — the edit is invisible in the data, and `hashesMatch`
is what gives it away.

### Filters

```bash
curl -s "http://localhost:5310/api/audit?entityId=<supplierId>&take=20"
```

`entityId`, `correlationId` and `actor`. `correlationId` is the useful one: it follows a single
upload across all eight services.

## The compliance report

Reporting turns a supplier's position into the PDF a buyer forwards to an auditor (FR-6.1). Unlike
every other service it reads its facts synchronously from Compliance and Registry rather than from a
local copy — ADR-0006 explains why at length, and it is the only place in Certiflow that does this.

Generation is asynchronous, so the request returns immediately:

```bash
curl -s -X POST http://localhost:5320/api/reports/suppliers/<supplierId> -H "Content-Type: application/json" -d '{"requestedBy":"buyer@acme.example"}'
```

`202 Accepted` with a report id. Poll it — a report takes a few seconds:

```bash
curl -s http://localhost:5320/api/reports/<reportId>
```

`Completed` brings a `verificationHash` and a `downloadUrl`. The download hands back a 15-minute SAS
rather than the bytes, for the same reason document downloads do:

```bash
curl -s http://localhost:5320/api/reports/<reportId>/download
```

### Verification

```bash
curl -s http://localhost:5320/api/reports/<reportId>/verify
```

Recomputes the fingerprint from the supplier's position **now** and compares it to what the report
attested to. `stillAccurate: false` does not mean the file was tampered with — it means the
supplier's compliance has changed since the report was issued, which is what someone holding an old
PDF needs to know. Approve another certificate for that supplier and watch it flip.

The hash covers the facts, not the file. Restyle the PDF and it is unchanged; alter a certificate
number and it is not. `daysRemaining` is deliberately excluded — it is derived from the expiry date
and today, so hashing it would make every report fail its own verification the next morning.

### When generation fails

Ask for a report on a supplier that does not exist:

```bash
curl -s -X POST http://localhost:5320/api/reports/suppliers/11111111-2222-3333-4444-555555555555 -H "Content-Type: application/json" -d '{"requestedBy":"buyer@acme.example"}'
```

The job comes back `Failed` with
`"compliance has no record at /api/suppliers/…/compliance"`, and `/download` answers `409`. The
message is not dead-lettered and the job is not stranded in `Generating` — a caller can always tell
a slow report from a dead one.

### Reports are immutable

Requesting twice produces two report ids, two blobs and two fingerprints. `GET
/api/reports/suppliers/{id}` lists them newest first. A report downloaded in March still says what
it said in March, which is the difference between an attestation and a dashboard (FR-6.5).

## Running everything at once

```bash
bash scripts/run-all.sh
```

Builds once, then starts the gateway, six APIs and the worker, and waits until all seven answer
`/health`.

The single build is not a convenience. Starting them with plain `dotnet run` makes six MSBuild
processes compile the same shared projects into the same `obj/` simultaneously, and they fail on
each other's file locks — which prints "the build failed" six times and is really a race.

## Notifications

Outbound email is **off**, and turning it on takes a deliberate config change
(`Notifications:OutboundEmailEnabled`). FR-7.8 is not a preference: a publicly reachable demo that
can send mail to any address anyone types is an open relay with extra steps. Every message is
recorded and shown in the in-app inbox instead, marked *held — not sent*.

```bash
curl -s http://localhost:5000/api/notifications -H "Authorization: Bearer <token>"
```

A supplier user sees only their own; the service narrows the query by the `supplier_id` claim rather
than trusting a parameter (NFR-8).

### Watching the reminders deduplicate

The expiry sweep runs on a timer — every two minutes in development, daily elsewhere — so a
certificate crossing into its renewal window raises `CertificateExpiringSoon` on *every* sweep.
FR-7.5 asks for one reminder per document per window, ever.

Approve a certificate with a corrected expiry inside a window (a reviewer can edit `expiresOn`),
then run the sweep by hand as many times as you like:

```bash
curl -s -X POST http://localhost:5000/api/expiry-watch -H "Authorization: Bearer <admin-token>" -H "Content-Type: application/json" -d '{}'
```

The inbox gains exactly one reminder and stays at one. That is a unique index on
`(document, window)`, not a check-then-insert — two deliveries of the same event would both pass a
check and both insert.
