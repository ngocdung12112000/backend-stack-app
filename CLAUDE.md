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

Still to do: gateway, backend compose, CI, api-client.
