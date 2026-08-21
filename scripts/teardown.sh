#!/usr/bin/env bash
#
# Deletes a Certiflow environment.
#
# SRS §20 R9 names the realistic failure of this project: "teardown discipline fails and the
# environment quietly stays up, billing". So teardown is a rehearsed, scripted step with an
# acceptance criterion (§18), not a thing someone remembers to do in the portal.
#
# What it deliberately does NOT delete: the Azure OpenAI account. It lives in its own resource
# group, it holds a model deployment that costs time and quota to recreate, and it bills per token
# rather than per hour - so leaving it up costs nothing while deleting it would make the next
# deployment a manual job again.
set -euo pipefail

ENVIRONMENT="${1:-}"

if [ -z "$ENVIRONMENT" ]; then
  echo "usage: teardown.sh <environment>   (e.g. teardown.sh dev)" >&2
  exit 64
fi

GROUP="rg-certiflow-${ENVIRONMENT}"

if ! az group show --name "$GROUP" --output none 2>/dev/null; then
  echo "$GROUP does not exist. Nothing to do."
  exit 0
fi

# A tag is a weak lock, but it is enough to stop this deleting a resource group somebody created by
# hand for something else that happens to share the naming convention.
MANAGED_BY=$(az group show --name "$GROUP" --query "tags.managedBy" --output tsv 2>/dev/null || echo "")

if [ "$MANAGED_BY" != "bicep" ]; then
  echo "Refusing: $GROUP is not tagged managedBy=bicep (found '${MANAGED_BY:-none}')." >&2
  echo "If this really is a Certiflow environment, tag it and run again." >&2
  exit 1
fi

# Belt as well as braces. The deploy workflow refuses to deploy into the OpenAI account's group,
# but teardown is also run by hand - and by hand is exactly when the wrong environment name gets
# typed. Deleting the account would cost quota and a manual rebuild, so it is worth one query.
PROTECTED=$(az resource list --resource-group "$GROUP" \
  --resource-type "Microsoft.CognitiveServices/accounts" --query "length(@)" --output tsv 2>/dev/null || echo "0")

if [ "$PROTECTED" != "0" ]; then
  echo "Refusing: $GROUP contains $PROTECTED Cognitive Services account(s)." >&2
  echo "The Azure OpenAI account holds a model deployment that costs time and quota to recreate," >&2
  echo "and it is meant to outlive any environment. Move it out, or delete it deliberately." >&2
  exit 1
fi

echo "About to delete every resource in $GROUP:"
az resource list --resource-group "$GROUP" --query "[].{name:name,type:type}" --output table || true
echo

if [ "${CERTIFLOW_TEARDOWN_YES:-}" != "true" ]; then
  read -r -p "Type the environment name to confirm: " CONFIRM
  if [ "$CONFIRM" != "$ENVIRONMENT" ]; then
    echo "Not confirmed. Nothing deleted."
    exit 1
  fi
fi

# --no-wait would return in seconds and leave the caller believing the meter had stopped. It has
# not: deletion takes minutes, and an environment half-deleted is an environment still billing.
# Waiting is the entire point of a teardown script.
echo "Deleting $GROUP. This takes a few minutes..."
az group delete --name "$GROUP" --yes

echo
echo "Deleted. Confirm nothing is left:"
az group exists --name "$GROUP"
