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

## Running the intake service

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5280 dotnet run --project src/services/document-intake/Certiflow.Intake.Api
```

The schema is created on startup in development via `EnsureCreated`. Real environments migrate as a
deploy step: letting a scaled-out service race to alter its own schema on startup is how a
deployment corrupts one.

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
