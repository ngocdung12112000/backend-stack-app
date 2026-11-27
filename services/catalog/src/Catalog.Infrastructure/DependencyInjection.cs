using BuildingBlocks.Security;
using Catalog.Application.Abstractions;
using Catalog.Infrastructure.Caching;
using Catalog.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Catalog.Infrastructure;

public static class DependencyInjection
{
    /// <summary>MongoDB (<c>ConnectionStrings:Catalog</c>, <c>Mongo:Database</c>) and Redis (<c>Redis:ConnectionString</c>).</summary>
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<MongoOptions>()
            .Configure(o =>
            {
                config.GetSection(MongoOptions.Section).Bind(o);
                o.ConnectionString = config.GetConnectionString("Catalog") ?? o.ConnectionString;
            })
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "ConnectionStrings:Catalog (MongoDB) is required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Database), "Mongo:Database is required.")
            .ValidateOnStart();

        services.AddOptions<CatalogOptions>()
            .Bind(config.GetSection(CatalogOptions.Section))
            .Validate(o => o.ProductCacheMinutes > 0 && o.CategoryTreeCacheMinutes > 0 && o.ProductCacheJitterSeconds >= 0,
                "Catalog cache durations must be positive.")
            .Validate(o => o.Currency.Length == 3, "Catalog:Currency must be a 3-letter ISO code.")
            .ValidateOnStart();

        services.AddSingleton<IMongoClient>(sp =>
        {
            var settings = MongoClientSettings.FromConnectionString(sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString);
            settings.ApplicationName = "catalog";
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
            return new MongoClient(settings);
        });
        services.AddSingleton<CatalogDb>();
        services.AddHostedService<MongoIndexInitializer>();
        services.AddSingleton<IProductRepository, ProductRepository>();
        services.AddSingleton<ICategoryRepository, CategoryRepository>();
        services.AddSingleton<IBrandRepository, BrandRepository>();

        services.AddRedisConnection(config);
        services.AddSingleton<ICatalogCache, RedisCatalogCache>();

        services.AddHealthChecks()
            .AddCheck<MongoReadinessCheck>("mongodb", tags: ["ready"])
            .AddCheck<RedisReadinessCheck>("redis", tags: ["ready"]);
        return services;
    }
}
