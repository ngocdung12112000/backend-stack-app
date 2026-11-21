using BuildingBlocks.Observability;
using BuildingBlocks.Security;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("catalog");
builder.Services.AddJwtValidation(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new OpenApiInfo { Title = "Catalog API", Version = "v1" }));

var app = builder.Build();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

app.MapObservability();

// Map the service's endpoints here, e.g. app.MapGroup("/api/v1/<resource>").

app.Run();

public partial class Program;
