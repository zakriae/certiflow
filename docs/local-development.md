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

Six services. Start order does not matter — each creates its own schema, and the compliance state
reconciles itself on read if the profile and the supplier arrive out of order (ADR-0005 covers the
related delivery hazard).

| Service | Port |
|---|---|
| Supplier Registry | 5270 |
| Document Intake | 5280 |
| Verification | 5290 |
| Compliance | 5300 |
| Audit Trail | 5310 |
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

### When a context gains a table

The dev bootstrap creates a schema that is *missing*; it cannot evolve one that already exists.
Add a table to a context whose schema is already there and nothing happens — until a query hits
`Invalid object name` one table later. Until EF migrations exist, recreate the schema:

```bash
CERTIFLOW_RECREATE_SCHEMA=true DOTNET_ENVIRONMENT=Development dotnet run --project src/services/document-intelligence/Certiflow.Intelligence.Worker
```

Safe because every byte of data here is generated (NFR-11). Real environments run EF migrations as
a deploy step (NFR-19).

Each service creates **its own schema** on startup in development, not the whole database.
`EnsureCreated` cannot be used here: eight contexts share one database (SRS §13.1) and
`EnsureCreated` is all-or-nothing *per database*, so the first service to start creates everything
it knows about and every context after it finds the database already present and creates nothing.
That failure is silent until a consumer hits `Invalid object name` at runtime. Real environments run
EF migrations as a deploy step (NFR-19).

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
cd src/web/certiflow-web && nvm use && npm install && npm start
```

The dev server proxies `/api/*` to the five services (`proxy.conf.json`) so the SPA talks to one
origin. **A YARP gateway replaces this proxy when deployed** — the proxy is a dev convenience, not
the architecture.

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
