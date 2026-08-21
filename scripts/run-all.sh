#!/usr/bin/env bash
# Starts the gateway, all six APIs and the worker.
#
# Builds once first and starts every service with --no-build. Starting them with plain `dotnet run`
# makes six MSBuild processes compile the same shared projects into the same obj/ at the same time,
# and they fail on each other's file locks - which reads as "the build failed" and is really a race.
set -uo pipefail
cd "$(dirname "$0")/.."

dotnet build --nologo -v quiet || { echo "build failed"; exit 1; }

run() { ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$2" \
        dotnet run --project "$1" --no-build --no-launch-profile > "/tmp/certiflow-$3.log" 2>&1 & }

run src/gateway/Certiflow.Gateway                                  5000 gateway
run src/services/supplier-registry/Certiflow.SupplierRegistry.Api  5270 registry
run src/services/document-intake/Certiflow.Intake.Api              5280 intake
run src/services/verification/Certiflow.Verification.Api           5290 verification
run src/services/compliance/Certiflow.Compliance.Api               5300 compliance
run src/services/audit-trail/Certiflow.Audit.Api                   5310 audit
run src/services/reporting/Certiflow.Reporting.Api                 5320 reporting

DOTNET_ENVIRONMENT=Development dotnet run --project src/services/document-intelligence/Certiflow.Intelligence.Worker \
  --no-build > /tmp/certiflow-worker.log 2>&1 &

for _ in $(seq 1 40); do
  sleep 3
  ok=""
  for p in 5000 5270 5280 5290 5300 5310 5320; do
    ok="$ok$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "http://localhost:$p/health" 2>/dev/null)"
  done
  if [ "$ok" = "200200200200200200200" ]; then echo "all seven up"; exit 0; fi
done

echo "not all services came up: $ok"
exit 1
