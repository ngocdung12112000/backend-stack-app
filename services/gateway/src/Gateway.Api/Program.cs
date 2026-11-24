using BuildingBlocks.Observability;
using BuildingBlocks.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("gateway");
// Validates RS256 tokens against identity's JWKS (Jwt:JwksUrl) and rejects revoked jti values (Redis auth:revoked-jti:*).
builder.Services.AddJwtValidation(builder.Configuration);
builder.Services.AddJtiBlacklist(builder.Configuration);
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Aggregated API docs: each service's swagger.json is proxied under /docs/<service>/swagger.json
// (see the "docs-*" routes in appsettings.json). Add one SwaggerEndpoint per service.
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/docs/catalog/swagger.json", "Catalog");
    options.SwaggerEndpoint("/docs/identity/swagger.json", "Identity");
});

app.MapObservability();
app.MapReverseProxy();

app.Run();

public partial class Program;
