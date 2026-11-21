# CLAUDE.md (stackshop-backend)

Phase 0 foundation. `StackShop.Backend.sln` (everything) plus a per-service `services/catalog/Catalog.sln`.

- `building-blocks/`: `Common` (Result, Error, PagedResult, IClock), `Observability` (Serilog, OTel, /health/live, /health/ready, /metrics), `Security` (RS256 JWT validation), `Messaging` (interfaces only).
- `services/catalog/`: Domain -> Application (MediatR) -> Infrastructure (in-memory placeholder repo) -> Api (Minimal API, Swagger), `tests/Catalog.Tests`, `Dockerfile`.
- Central package versions in `Directory.Packages.props`; shared settings in `Directory.Build.props`.

```bash
dotnet build
dotnet test
dotnet run --project services/catalog/src/Catalog.Api
docker build -f services/catalog/Dockerfile -t stackshop/catalog:dev .   # context = repo root
```

- `tests/Architecture.Tests`: csproj-based rules (services never reference each other, building blocks never reference services, Domain/Application/Infrastructure layer direction). New services are picked up automatically.

- `tools/templates/stackshop-service`: `dotnet new stackshop-service -n Ordering -o services/ordering` (installed via `dotnet new install tools/templates/stackshop-service`). `pwsh tools/new-service.ps1 -Name Ordering` does both and adds the projects to the root sln. If PowerShell blocks the script (execution policy), run the two `dotnet` commands by hand.

- `services/gateway`: YARP (`Gateway.Api`, `Gateway.Tests`, `Gateway.sln`, Dockerfile). Routes in `appsettings.json` under `ReverseProxy`; `/api/v1/products/**` -> catalog, inbound `X-User-*` headers stripped. Catalog default address is `http://localhost:5001/`; compose overrides it to `http://catalog:8080/`.
- Gateway Swagger UI: `http://localhost:5000/swagger`. Each service's `swagger.json` is proxied at `/docs/<service>/swagger.json` (a `docs-<service>` route in `appsettings.json`) and listed in `UseSwaggerUI` in `Program.cs`. When adding a service, add both. It is not gated by environment yet, so disable or restrict it before exposing the gateway publicly.
- `docker-compose.yml`: catalog + gateway (gateway on host `:5000`), joins the external `stackshop` network created by `stackshop-infra/local/compose.yml`. Start infra first.
- Running locally without Docker: `ASPNETCORE_URLS=http://localhost:5001 dotnet run --no-launch-profile --project services/catalog/src/Catalog.Api`, same with `:5000` for the gateway. (`--no-launch-profile` is needed because catalog has a launchSettings.json.)

Still to do: CI, api-client.
