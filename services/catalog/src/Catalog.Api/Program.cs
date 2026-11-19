using BuildingBlocks.Observability;
using BuildingBlocks.Security;
using Catalog.Application.Products;
using Catalog.Infrastructure;
using MediatR;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("catalog");
builder.Services.AddJwtValidation(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddMediatR(c => c.RegisterServicesFromAssemblyContaining<ListProductsQuery>());
builder.Services.AddCatalogInfrastructure();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

app.MapObservability();

app.MapGet("/api/v1/products", async (ISender sender, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        Results.Ok(await sender.Send(new ListProductsQuery(page, pageSize), ct)))
    .WithName("ListProducts")
    .WithTags("Products");

app.Run();

public partial class Program;
