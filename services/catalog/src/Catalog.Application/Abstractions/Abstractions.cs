using System.Diagnostics.Metrics;
using Catalog.Domain;

namespace Catalog.Application.Abstractions;

public enum ProductSort { Newest, PriceAsc, PriceDesc, Name }

/// <summary>Filter for product listings. <see cref="CategoryIds"/> already includes descendants; empty means "match nothing".</summary>
public sealed record ProductQuery(
    ProductStatus? Status, IReadOnlyList<Guid>? CategoryIds, string? BrandSlug, string? Text, ProductSort Sort, int Page, int PageSize);

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Product?> GetPublishedBySlugAsync(string slug, CancellationToken ct);
    /// <summary>Published products only, returned in the order of <paramref name="ids"/>.</summary>
    Task<IReadOnlyList<Product>> GetPublishedByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct);
    Task<(IReadOnlyList<Product> Items, long Total)> QueryAsync(ProductQuery query, CancellationToken ct);

    Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct);
    Task<IReadOnlySet<string>> SlugsStartingWithAsync(string prefix, CancellationToken ct);
    /// <summary>Id of the product that owns <paramref name="sku"/> (any product, any status), or null.</summary>
    Task<Guid?> FindProductIdBySkuAsync(string sku, CancellationToken ct);
    Task<bool> AnyInCategoryAsync(Guid categoryId, CancellationToken ct);
    Task<bool> AnyWithBrandAsync(Guid brandId, CancellationToken ct);

    /// <exception cref="CatalogException">CATALOG_SLUG_TAKEN / CATALOG_SKU_TAKEN when a unique index rejects the write.</exception>
    Task InsertAsync(Product product, CancellationToken ct);
    /// <summary>Replaces the document only if it is still at <paramref name="expectedVersion"/> (BR-08).</summary>
    /// <exception cref="CatalogException">CATALOG_VERSION_MISMATCH (412) when someone else saved first.</exception>
    Task ReplaceAsync(Product product, long expectedVersion, CancellationToken ct);
    /// <summary>Refreshes the denormalised brand reference on every product of the brand; returns their slugs for cache invalidation.</summary>
    Task<IReadOnlyList<string>> UpdateBrandRefAsync(BrandRef brand, CancellationToken ct);
}

public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Category?> GetBySlugAsync(string slug, CancellationToken ct);
    Task<IReadOnlyList<Category>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct);
    Task<IReadOnlyList<Category>> ListAsync(bool activeOnly, CancellationToken ct);
    /// <summary>Ids of the active category with this slug and of its active descendants. Empty when the slug is unknown.</summary>
    Task<IReadOnlyList<Guid>> GetSelfAndDescendantIdsAsync(string slug, CancellationToken ct);
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct);
    Task<IReadOnlySet<string>> SlugsStartingWithAsync(string prefix, CancellationToken ct);
    Task<bool> HasChildrenAsync(Guid id, CancellationToken ct);
    Task InsertAsync(Category category, CancellationToken ct);
    Task ReplaceAsync(Category category, long expectedVersion, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface IBrandRepository
{
    Task<Brand?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Brand>> ListAsync(bool activeOnly, CancellationToken ct);
    Task<IReadOnlySet<string>> SlugsStartingWithAsync(string prefix, CancellationToken ct);
    Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct);
    Task InsertAsync(Brand brand, CancellationToken ct);
    Task ReplaceAsync(Brand brand, long expectedVersion, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>Redis cache-aside with stampede protection. Failures of the cache never fail a request.</summary>
public interface ICatalogCache
{
    /// <summary>Returns the cached value, or runs <paramref name="factory"/> once (per key, across instances) and caches a non-null result.</summary>
    Task<T?> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T?>> factory, CancellationToken ct) where T : class;
    Task RemoveAsync(IEnumerable<string> keys, CancellationToken ct);
}

public static class CacheKeys
{
    public const string CategoryTree = "cache:category-tree";
    public static string Product(string slug) => $"cache:product:{slug}";
}

public sealed class CatalogOptions
{
    public const string Section = "Catalog";

    public int ProductCacheMinutes { get; set; } = 10;
    public int ProductCacheJitterSeconds { get; set; } = 60;
    public int CategoryTreeCacheMinutes { get; set; } = 60;
    public string Currency { get; set; } = "USD";
}

public static class CatalogMetrics
{
    public const string MeterName = "StackShop.Catalog";
    private static readonly Meter Meter = new(MeterName);

    /// <summary>cache = product|category_tree, result = hit|miss. Never labelled with ids or slugs.</summary>
    public static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>("catalog_cache_requests_total");
    public static readonly Counter<long> StatusChanges = Meter.CreateCounter<long>("catalog_product_status_changes_total");
}
