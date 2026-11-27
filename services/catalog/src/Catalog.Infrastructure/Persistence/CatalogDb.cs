using Catalog.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace Catalog.Infrastructure.Persistence;

public sealed class MongoOptions
{
    public const string Section = "Mongo";

    /// <summary>Bound from <c>ConnectionStrings:Catalog</c>. Transactions (Phase 2 outbox) need a replica set, e.g. <c>?replicaSet=rs0</c>.</summary>
    public string ConnectionString { get; set; } = string.Empty;
    public string Database { get; set; } = "catalog";

    /// <summary>Creates the indexes at start-up (idempotent). Only tooling that boots the app without a database (OpenAPI generation) turns this off.</summary>
    public bool CreateIndexesOnStartup { get; set; } = true;
}

/// <summary>Class maps and serialisers for the Domain types. The Domain stays free of Mongo attributes.</summary>
public static class CatalogMongoMappings
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;
            _registered = true;

            // Ids as readable strings (GUID v7 sorts by creation time); money as Decimal128 so $sort/$gt work numerically.
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(BsonType.String));
            BsonSerializer.TryRegisterSerializer(new DecimalSerializer(BsonType.Decimal128));

            ConventionRegistry.Register("catalog", new ConventionPack
            {
                new CamelCaseElementNameConvention(),
                new IgnoreExtraElementsConvention(true),   // newer pods may write fields older pods do not know yet
                new EnumRepresentationConvention(BsonType.String),
            }, t => t.Namespace == typeof(Product).Namespace);
        }
    }
}

public sealed class CatalogDb
{
    public const string ProductsCollection = "products";
    public const string CategoriesCollection = "categories";
    public const string BrandsCollection = "brands";

    public CatalogDb(IMongoClient client, IOptions<MongoOptions> options)
    {
        CatalogMongoMappings.Register();
        Database = client.GetDatabase(options.Value.Database);
        Products = Database.GetCollection<Product>(ProductsCollection);
        Categories = Database.GetCollection<Category>(CategoriesCollection);
        Brands = Database.GetCollection<Brand>(BrandsCollection);
    }

    public IMongoDatabase Database { get; }
    public IMongoCollection<Product> Products { get; }
    public IMongoCollection<Category> Categories { get; }
    public IMongoCollection<Brand> Brands { get; }

    public const string SlugIndex = "ux_products_slug";
    public const string SkuIndex = "ux_products_variants_sku";

    /// <summary>
    /// Creates every index. Idempotent: re-creating an index with the same definition is a no-op, so it is safe on every start
    /// and from several pods at once. Document shape changes go through <c>schemaVersion</c> (see docs/03 section 3.1).
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        var p = Builders<Product>.IndexKeys;
        await Products.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Product>(p.Ascending(x => x.Slug), new CreateIndexOptions { Name = SlugIndex, Unique = true }),
            // Unique SKU across all products. Partial: products without variants (an empty array indexes as one null key)
            // must not collide with each other.
            new CreateIndexModel<Product>(p.Ascending("variants.sku"), new CreateIndexOptions<Product>
            {
                Name = SkuIndex, Unique = true,
                PartialFilterExpression = new BsonDocument("variants.sku", new BsonDocument("$type", "string")),
            }),
            new CreateIndexModel<Product>(p.Ascending(x => x.CategoryIds).Ascending(x => x.Status), new CreateIndexOptions { Name = "ix_products_category_status" }),
            new CreateIndexModel<Product>(p.Ascending("brand.id"), new CreateIndexOptions { Name = "ix_products_brand_id" }),
            new CreateIndexModel<Product>(p.Ascending("brand.slug").Ascending(x => x.Status), new CreateIndexOptions { Name = "ix_products_brand_slug_status" }),
            new CreateIndexModel<Product>(p.Ascending(x => x.Status).Descending(x => x.PublishedAt), new CreateIndexOptions { Name = "ix_products_status_published" }),
            new CreateIndexModel<Product>(p.Ascending(x => x.Status).Ascending(x => x.MinPrice), new CreateIndexOptions { Name = "ix_products_status_minprice" }),
            new CreateIndexModel<Product>(p.Ascending(x => x.Status).Descending(x => x.UpdatedAt), new CreateIndexOptions { Name = "ix_products_status_updated" }),
        ], ct);

        var c = Builders<Category>.IndexKeys;
        await Categories.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Category>(c.Ascending(x => x.Slug), new CreateIndexOptions { Name = "ux_categories_slug", Unique = true }),
            new CreateIndexModel<Category>(c.Ascending(x => x.ParentId).Ascending(x => x.Sort), new CreateIndexOptions { Name = "ix_categories_parent_sort" }),
            new CreateIndexModel<Category>(c.Ascending(x => x.Path), new CreateIndexOptions { Name = "ix_categories_path" }),
        ], ct);

        var b = Builders<Brand>.IndexKeys;
        await Brands.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<Brand>(b.Ascending(x => x.Slug), new CreateIndexOptions { Name = "ux_brands_slug", Unique = true }),
            new CreateIndexModel<Brand>(b.Ascending(x => x.IsActive).Ascending(x => x.Name), new CreateIndexOptions { Name = "ix_brands_active_name" }),
        ], ct);
    }
}

/// <summary>Creates the indexes before the service takes traffic; waits for Mongo to come up (compose start-up race).</summary>
public sealed class MongoIndexInitializer(CatalogDb db, IOptions<MongoOptions> options, ILogger<MongoIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!options.Value.CreateIndexesOnStartup) return;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.EnsureIndexesAsync(ct);
                logger.LogInformation("Catalog MongoDB indexes are in place");
                return;
            }
            catch (Exception ex) when (attempt < 30 && ex is not OperationCanceledException)
            {
                logger.LogWarning("MongoDB not ready for index creation (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class MongoReadinessCheck(CatalogDb db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await db.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB is unreachable", ex);
        }
    }
}
