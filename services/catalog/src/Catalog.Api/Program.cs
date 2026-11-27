using BuildingBlocks.Observability;
using BuildingBlocks.Security;
using Catalog.Api;
using Catalog.Api.Endpoints;
using Catalog.Application;
using Catalog.Application.Abstractions;
using Catalog.Infrastructure;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("catalog", CatalogMetrics.MeterName);
builder.Services.AddJwtValidation(builder.Configuration); // RS256 via identity's JWKS (Jwt:JwksUrl); no jti blacklist here, the gateway enforces it
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddCatalogApplication();
builder.Services.AddCatalogInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Catalog API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Access token from /api/v1/auth/login (admin endpoints need catalog:write)",
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

app.MapObservability();
app.MapCatalogEndpoints();

app.Run();

public partial class Program;
