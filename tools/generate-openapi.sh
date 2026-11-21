#!/usr/bin/env bash
# Regenerates contracts/openapi/<service>.json for every service that exposes Swagger.
# Run from anywhere (Git Bash, WSL, CI):  bash tools/generate-openapi.sh
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet tool restore

for api in services/*/src/*.Api; do
  svc=$(basename "$(dirname "$(dirname "$api")")")
  name=$(basename "$api")
  [ "$svc" = "gateway" ] && continue   # the gateway only proxies other services' documents

  dotnet build "$api" -c Release --nologo -v quiet
  dotnet swagger tofile --output "contracts/openapi/$svc.json" "$api/bin/Release/net8.0/$name.dll" v1
done
