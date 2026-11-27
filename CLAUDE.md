# CLAUDE.md (stackshop-backend)

Phase 0 foundation. `StackShop.Backend.sln` (everything) plus a per-service `services/catalog/Catalog.sln`.

- `building-blocks/`: `Common` (Result, Error, PagedResult, IClock), `Observability` (Serilog, OTel, /health/live, /health/ready, /metrics), `Security` (RS256 JWT validation), `Messaging` (interfaces only).
- `services/catalog/`: Domain -> Application (MediatR) -> Infrastructure (MongoDB + Redis, F03) -> Api (Minimal API, Swagger), `tests/Catalog.Tests`, `tests/Catalog.IntegrationTests`, `Dockerfile`. See the F03 entry below.
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

- `services/identity` (F01): Domain/Application/Infrastructure/Api + `Identity.Tests` (unit, no I/O) and `Identity.IntegrationTests` (Testcontainers MySQL 8.4 + Redis 7, needs Docker). MySQL schema `identity` via EF Core + Pomelo; migrations in `Identity.Infrastructure/Persistence/Migrations` (`dotnet tool restore && dotnet ef migrations add <Name> --project services/identity/src/Identity.Infrastructure --startup-project services/identity/src/Identity.Api --output-dir Persistence/Migrations`). Apply with `dotnet Identity.Api.dll migrate` (K8s Job) or `Database__MigrateOnStartup=true` (compose/dev only). First admin: `IDENTITY_ADMIN_PASSWORD=... dotnet Identity.Api.dll create-admin --email=...`.
- Identity config: `ConnectionStrings__Identity`, `Redis__ConnectionString`, `Email__Host/Port` (Mailpit locally), `Auth__FrontendBaseUrl`, and the RS256 key via `Signing__PrivateKeyPem` or `Signing__PrivateKeyPath` (Secret). `Signing__GenerateIfMissing=true` is dev only (compose keeps the generated key on the `identity-keys` volume). Keys are published at `/.well-known/jwks.json`; the gateway (`Jwt__JwksUrl`) and services validate with it, and the gateway/identity also reject `jti` values in Redis `auth:revoked-jti:*`.
- `building-blocks/Security`: `AddJwtValidation` (JWKS or PEM, no claim remapping: `sub`, `role`, `perm`, `jti`), `perm:<resource>:<action>` policies, `AddJtiBlacklist` (Redis, fails open on Redis errors).

- `services/catalog` (F03, Phase 1): MongoDB via the official driver (class maps in `Catalog.Infrastructure/Persistence/CatalogDb.cs`, Domain stays attribute-free), Redis cache-aside with a SET NX lock against stampedes (`Caching/RedisCatalogCache.cs`). Domain rules throw `CatalogException` (code + kind + field errors), mapped to ProblemDetails in `Catalog.Api/Http.cs`. Admin endpoints under `/api/v1/admin/*` need `perm:catalog:write` and `If-Match` (ETag = version, 412 `CATALOG_VERSION_MISMATCH`, 428 when missing). Indexes are created at startup (`Mongo__CreateIndexesOnStartup=false` only for OpenAPI generation). Tests: `Catalog.Tests` (unit, in-memory fakes) and `Catalog.IntegrationTests` (Testcontainers `mongo:7` replica set + Redis 7, needs Docker).
- Catalog config: `ConnectionStrings__Catalog` (compose: `mongodb://mongo:27017/?replicaSet=rs0`; host tools: `mongodb://localhost:27017/?directConnection=true`), `Mongo__Database` (`catalog`), `Redis__ConnectionString`, `Jwt__JwksUrl` (identity JWKS), optional `Catalog__ProductCacheMinutes|ProductCacheJitterSeconds|CategoryTreeCacheMinutes|Currency`.

- `.github/workflows/ci.yml`: `detect` (changed services; shared code such as building-blocks, contracts, Directory.*.props marks all as changed; uses git diff + jq, not dorny/paths-filter, so new services need no workflow edit) -> `architecture` (always) -> `service` matrix per changed service (restore, `dotnet format --verify-no-changes`, build, test + coverage) -> `ci-ok` (single required check). No integration tests or Testcontainers yet. Not run on GitHub yet; `actionlint` is not installed locally.

- OpenAPI: `bash tools/generate-openapi.sh` writes `contracts/openapi/<service>.json` (Swashbuckle CLI from `.config/dotnet-tools.json`; the gateway is skipped). Commit the snapshot with any API change; `publish-client.yml` fails if it is stale. A service needs `SwaggerDoc("v1", ...)` and `.Produces<T>()` on endpoints for useful types.
- `clients/api-client`: `@ngocdung12112000/api-client` (openapi-typescript + openapi-fetch). `npm ci && npm run build` generates `src/generated` (gitignored) and compiles to `dist`. Usage: `createApiClient({ baseUrl: "http://localhost:5000" }).catalog.GET("/api/v1/products")`. Version lives in `package.json`; bump it with the API change.
- `.github/workflows/publish-client.yml`: on `main`, regenerate OpenAPI, check the snapshot, build, `npm publish` to GitHub Packages if that version is new (uses `GITHUB_TOKEN`; needs the GitHub repo, and the package scope must match the owner). Never run on GitHub yet.

Phase 0 backend checklist is complete locally; remaining work is pushing to GitHub and watching the first CI and publish runs.
