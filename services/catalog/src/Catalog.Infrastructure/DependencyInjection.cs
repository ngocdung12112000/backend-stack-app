using Catalog.Application.Products;
using Catalog.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Phase 0: in-memory empty store. Replaced by MongoDB in F03.</summary>
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IProductRepository, InMemoryProductRepository>();
        return services;
    }
}

internal sealed class InMemoryProductRepository : IProductRepository
{
    private readonly List<Product> _products = [];

    public Task<IReadOnlyList<Product>> ListAsync(int page, int pageSize, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Product>>(_products.Skip((page - 1) * pageSize).Take(pageSize).ToList());

    public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)_products.Count);
}
