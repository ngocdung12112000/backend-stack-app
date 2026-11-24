using BuildingBlocks.Common;
using BuildingBlocks.Observability;
using BuildingBlocks.Security;
using Identity.Api;
using Identity.Api.Endpoints;
using Identity.Application;
using Identity.Application.Users;
using Identity.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("identity", IdentityMetrics.MeterName);
builder.Services.AddJwtValidation(builder.Configuration); // before infrastructure: that registers identity's own signing key on top
builder.Services.AddIdentityInfrastructure(builder.Configuration);
builder.Services.AddIdentityApplication();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Identity is only reachable through the gateway (network policy), so X-Forwarded-For from it is trusted.
// This is what lets the per-IP login throttle see the real client address.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Identity API", Version = "v1" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Access token from /api/v1/auth/login",
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
    o.CustomSchemaIds(t => t.Name);
});

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

var app = builder.Build();

// One-shot CLI modes share this binary and its configuration:
//   dotnet Identity.Api.dll migrate                       apply EF migrations (the Kubernetes migration Job)
//   IDENTITY_ADMIN_PASSWORD=... dotnet Identity.Api.dll create-admin --email=a@b.c --name="Admin"   (BR-08)
switch (args.FirstOrDefault())
{
    case "migrate":
        await DependencyInjection.MigrateAsync(app.Services);
        return 0;
    case "create-admin":
        await DependencyInjection.MigrateAsync(app.Services);
        return await CreateAdminAsync(app, args);
}

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
    await DependencyInjection.MigrateAsync(app.Services);

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

app.MapObservability();
app.MapIdentityEndpoints();

app.Run();
return 0;

static async Task<int> CreateAdminAsync(WebApplication app, string[] args)
{
    string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..];

    var email = Arg("email") ?? Environment.GetEnvironmentVariable("IDENTITY_ADMIN_EMAIL");
    var name = Arg("name") ?? Environment.GetEnvironmentVariable("IDENTITY_ADMIN_NAME") ?? "Administrator";
    var password = Environment.GetEnvironmentVariable("IDENTITY_ADMIN_PASSWORD"); // never a CLI argument: it would land in shell history
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
    {
        Console.Error.WriteLine("Usage: IDENTITY_ADMIN_PASSWORD=... create-admin --email=<email> [--name=<full name>]");
        return 2;
    }

    using var scope = app.Services.CreateScope();
    try
    {
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateAdminCommand(email, password, name));
        Console.WriteLine(result.IsSuccess ? $"Admin created: {result.Value}" : $"Failed: {result.Error.Code}");
        return result.IsSuccess ? 0 : 1;
    }
    catch (FluentValidation.ValidationException ex)
    {
        Console.Error.WriteLine("Failed: " + string.Join("; ", ex.Errors.Select(e => e.ErrorMessage)));
        return 1;
    }
}

public partial class Program;
