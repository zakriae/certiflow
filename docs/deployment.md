# Deploying Certiflow

The environment exists for the length of a recording session and is torn down afterwards (SRS §5.0).
NFR-3 is the requirement that shapes everything here: the full stack must come back from `main` plus
Bicep in **under 30 minutes, unattended**.

Nothing is deployed automatically. `Deploy` is `workflow_dispatch` only, because deploying on every
push to `main` means every merge silently starts a meter — which SRS §20 R9 names as the realistic
failure of this project.

## One-time setup (needs the subscription owner)

These four steps cannot be scripted from inside the repo: they create the trust relationship that
lets GitHub deploy without a secret.

### 1. Create an Entra app registration and a federated credential

```bash
az ad app create --display-name certiflow-deploy --query appId -o tsv
```

Take the `appId` it prints and create the service principal:

```bash
az ad sp create --id <appId>
```

Then add the federated credential, which is what removes the need for a client secret. **Replace
`<owner>` with your GitHub username**:

```bash
az ad app federated-credential create --id <appId> --parameters '{"name":"certiflow-main","issuer":"https://token.actions.githubusercontent.com","subject":"repo:<owner>/certiflow:ref:refs/heads/main","audiences":["api://AzureADTokenExchange"]}'
```

The `subject` is exact: a workflow running on any other branch will not be trusted, which is the
point.

### 2. Give it permission to deploy

```bash
az role assignment create --assignee <appId> --role Contributor --scope /subscriptions/<subscriptionId>
```

Contributor is not enough on its own. The Bicep creates role assignments — each service gets its own
managed identity with its own permissions — and creating role assignments requires:

```bash
az role assignment create --assignee <appId> --role "Role Based Access Control Administrator" --scope /subscriptions/<subscriptionId>
```

### 3. Add the repository secrets

`Settings → Secrets and variables → Actions`:

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | the `appId` from step 1 |
| `AZURE_TENANT_ID` | your tenant id |
| `AZURE_SUBSCRIPTION_ID` | your subscription id |
| `AZURE_SQL_ADMIN_OBJECT_ID` | your Entra object id |
| `AZURE_SQL_ADMIN_LOGIN` | your Entra sign-in name |

Print all four with:

```bash
az account show --query "{subscription:id, tenant:tenantId}" -o json && az ad signed-in-user show --query "{objectId:id, login:userPrincipalName}" -o json
```

They are deliberately **not** written into this file. None of them is a credential, but a public
repository is not the place for an account's identifiers and its owner's sign-in name — and a
document that ships with one person's values invites the next reader to paste them in unchanged.

The last two make **you** the SQL administrator. There is no SQL login and no password anywhere:
the server is created with Entra-only authentication, so there is nothing to rotate and nothing to
leak in a deployment log (NFR-9).

### 4. Set a budget alert

Not optional. SRS §20 R9: the realistic failure of this project is an environment that quietly stays
up and bills. Alerts at $15 and $20 on the subscription.

## Deploying

Actions → **Deploy** → Run workflow. Leave the environment name as `demo`.

Do **not** use `dev`: that resolves to `rg-certiflow-dev`, which holds the Azure OpenAI account, and
teardown deletes a resource group wholesale. The workflow refuses it, and so does the teardown
script.

The run does five things in order:

1. **Provision** — creates the resource group and everything in it from `infra/main.bicep`.
2. **Build and push** nine container images in parallel. One `Dockerfile` builds all nine; only the
   project path differs.
3. **Apply migrations** — once, from the workflow, never from a service. Container Apps runs several
   replicas, and several replicas applying the same migration is how a deployment corrupts a schema
   (NFR-19). The scripts are idempotent, so re-running a deployment is safe.
4. **Smoke test** — waits for the gateway, then asserts the OIDC discovery document is being served.
   If that is wrong every service fails to validate tokens, and it fails as a confusing 401 rather
   than an obvious misconfiguration.
5. **Summary** — prints the gateway URL and the teardown command.

Every service scales to zero, so the first request after a quiet period pays a cold start. The smoke
test both proves the deployment and warms the gateway, which is what §20 R8 asks for instead of
paying for a minimum replica around the clock.

## Tearing down

```bash
bash scripts/teardown.sh demo
```

It lists what it is about to delete, requires the environment name typed back, and **waits**.
`--no-wait` would return in seconds and leave you believing the meter had stopped; deletion takes
minutes, and a half-deleted environment is still billing.

It refuses any resource group not tagged `managedBy=bicep`, and refuses `rg-certiflow-dev` outright.

The Azure OpenAI account is never deleted. It lives outside every deployed environment, bills per
token rather than per hour, and recreating its model deployment costs time and quota.

## What has been verified, and what has not

The template **validates against the live subscription** — Azure accepted the whole thing, including
the role assignments, Container Apps environment, serverless SQL and Service Bus.

Not yet verified, because nothing has been deployed: the 30-minute unattended rebuild (NFR-3), the
teardown rehearsal, and the cold-start behaviour of a nine-service environment scaled to zero. Those
are acceptance criteria, and they are not met until a deployment has actually run.
