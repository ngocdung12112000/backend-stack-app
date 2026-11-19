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

Still to do: architecture test, `dotnet new` template, gateway, backend compose, CI, api-client.
