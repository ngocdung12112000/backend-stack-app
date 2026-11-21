using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Register EF Core / Mongo / Redis / broker clients here.</summary>
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services) => services;
}
