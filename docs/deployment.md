# Deploying Certiflow

The environment is **provisioned for a recording session and torn down afterwards** (SRS §5.0). That
is not cost-cutting theatre — it is the constraint that shapes the whole deployment: NFR-3 requires
the full stack back from `main` plus Bicep in under 30 minutes, unattended, and SRS §20 R9 names the
realistic failure as *"teardown discipline fails and the environment quietly stays up, billing"*.

So: deployment is manual-trigger only, teardown is a scripted step with an acceptance criterion, and
nothing in the stack survives except the one resource that should.

## What gets created

| Resource | Why this one |
|---|---|
| Container Apps environment + 8 apps | Gateway, six APIs, one worker. Scale to zero. |
| Azure SQL (serverless, GP_S_Gen5_1) | One database, a schema per context (SRS §13.1). Auto-pauses after 60 minutes. |
| Storage account | `documents` and `reports` containers, shared-key access **disabled**. |
| Service Bus (Standard) | Standard, not Basic: MassTransit's topology needs topics, and Basic has queues only. |
| Container Registry (Basic) | Admin user disabled; apps pull with managed identity. |
| Log Analytics + App Insights | 30-day retention — an environment that lives for hours has no use for 90. |

**Not created: the Azure OpenAI account.** It is referenced from its own resource group and
deliberately outlives every environment, because it holds a model deployment that costs time and
quota to rebuild and bills per token rather than per hour.

## Nothing holds a secret

Every service reaches SQL, Storage, Service Bus and Azure OpenAI with its **own** user-assigned
managed identity (NFR-9). There is no connection string with a password, no storage key, and no
Service Bus SAS rule anywhere in the templates — `disableLocalAuth`, `allowSharedKeyAccess: false`
and `azureADOnlyAuthentication` are set so those credentials cannot be created even by accident.

Eight identities rather than one shared identity. It costs nothing, and it means the audit service
cannot write to blob storage and the worker cannot read the review queue. One shared identity would
give every container the union of every permission, which is the same as giving them all the
maximum.

Only the **worker** holds `Cognitive Services OpenAI User`. Guardrail G1 and §13.4 both reduce to
"the thing that spends money should be the only thing that can", and this is where that stops being
a policy and becomes a permission.

GitHub authenticates to Azure with an OIDC federated credential, so no client secret exists to leak
or rotate.

## One-time setup

Create the federated credential and the repository secrets:

```bash
az ad app create --display-name certiflow-deploy
```

Then add a federated credential for `repo:<owner>/certiflow:ref:refs/heads/main`, grant the service
principal **Contributor** on the subscription (or on the environment resource groups plus
`Microsoft.Authorization/roleAssignments/write`), and set these repository secrets:

| Secret | What it is |
|---|---|
| `AZURE_CLIENT_ID` | App registration client id |
| `AZURE_TENANT_ID` | Directory tenant id |
| `AZURE_SUBSCRIPTION_ID` | Target subscription |
| `AZURE_SQL_ADMIN_OBJECT_ID` | Entra object id that becomes SQL admin |
| `AZURE_SQL_ADMIN_LOGIN` | Display name for that admin |

The SQL server has **no SQL login at all** — `azureADOnlyAuthentication` is on, so the admin is an
Entra principal and there is no password to store.

## Deploying

Actions → **Deploy** → Run workflow, with an environment name. Anything except `dev`, which is
where the OpenAI account lives; the workflow refuses that name rather than trusting whoever typed
it, because teardown deletes the environment's resource group wholesale.

The run does four things in order:

1. **Provision** — `az deployment group create` against `infra/main.bicep`.
2. **Build and push** eight images, in parallel, `fail-fast: false` so one failure does not hide the
   other seven.
3. **Apply migrations** — once, as a deploy step, never inside a service. Container Apps runs up to
   two replicas and two replicas applying the same migration concurrently is how a deployment
   corrupts a schema (NFR-19). `dotnet ef database update` is idempotent, so re-running a deployment
   is safe.
4. **Smoke test** — waits for `/health`, then asserts the gateway is serving its OIDC discovery
   document. If discovery is broken every service fails to validate tokens, and it fails as a
   confusing 401 rather than an obvious configuration error, so it is worth asserting explicitly.

The smoke test also *warms* the gateway, which is what §20 R8 asks for instead of paying for a
minimum replica around the clock.

## Cold starts are deliberate

Every app has `minReplicas: 0`. The first request after a quiet period pays a cold start. Paying for
a warm replica 24/7 to avoid that would cost more than the whole recording session.

**Warm the services by hitting them once before recording.**

## Tearing down

```bash
bash scripts/teardown.sh <environment>
```

It refuses to delete a group that is not tagged `managedBy=bicep`, and refuses again if the group
contains a Cognitive Services account. Both guards are deliberate: teardown is run by hand, and by
hand is exactly when the wrong environment name gets typed.

It does **not** pass `--no-wait`. Returning in seconds would let the caller believe the meter had
stopped when deletion takes minutes, and a half-deleted environment is still billing. Waiting is the
entire point of a teardown script.

```bash
az group list --query "[?starts_with(name,'rg-certiflow')].name" -o tsv
```

Run that after a session. It is the cheapest possible check against R9.

## Measured, not estimated

Rehearsed end to end on 2026-08-21 into `rg-certiflow-demo`, from an empty resource group.

| Step | Time |
|---|---|
| Pass one — everything except the container apps | 72 s |
| Build and push 8 images (4 concurrent, cold cache) | ~11 min |
| Pass two — the container apps | 132 s |
| Migrations, 7 contexts | 141 s |
| First healthy response from the gateway | < 30 s |

**Comfortably inside NFR-3's 30 minutes**, and the image build dominates it — on a GitHub runner
with a warm layer cache that step is far shorter, and the eight builds run in parallel rather than
four.

What the rehearsal proved beyond "it deploys": a profile published, a supplier registered, a
document uploaded by the supplier account, **a live gpt-5-mini extraction scoring 0.80**, the
uploader refused their own approval, a reviewer approving it, the supplier turning Compliant, a
report generated and downloaded from Blob Storage by user-delegation SAS, and an audit chain of 32
entries verifying valid.

It also found seven faults that no amount of template validation would have. They are listed in the
commit that fixed them; the short version is that **every one of them was invisible locally**,
because a laptop supplies a connection string for everything and Azure supplies a connection string
for nothing.

### Teardown takes longer than deletion appears to

`az group delete` returns when the group is gone, but the **Container Apps managed environment alone
takes 15–30 minutes**. Twenty-four of twenty-five resources were removed within about two minutes;
the environment held the group open long after everything billable inside it had stopped.

Do not interpret a slow teardown as a stuck one, and do not `--no-wait` it — the script waits on
purpose, because a half-deleted environment is still an environment.
