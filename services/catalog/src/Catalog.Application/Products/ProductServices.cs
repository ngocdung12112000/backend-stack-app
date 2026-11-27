using System.Text.Json;
using BuildingBlocks.Common;
using Catalog.Application.Abstractions;
using Catalog.Domain;
using Microsoft.Extensions.Options;

namespace Catalog.Application.Products;

public sealed record ProductDetailsRequest(
    string Name, string? Slug, string? Description, Guid? BrandId, IReadOnlyList<Guid>? CategoryIds, Guid? PrimaryCategoryId,
    IDictionary<string, JsonElement>? Attributes, IReadOnlyList<string>? Tags, SeoDto? Seo);

public sealed record OptionRequest(string Name, IReadOnlyList<string> Values);
public sealed record SetOptionsRequest(IReadOnlyList<OptionRequest> Options);
public sealed record VariantRequest(IDictionary<string, string>? Options, decimal Price, decimal? CompareAtPrice, int WeightG, string? Barcode, bool IsActive = true);
public sealed record ImageRequest(string Url, string? ThumbUrl, string? Alt);
public sealed record SetImagesRequest(IReadOnlyList<ImageRequest> Images);

/// <summary>Resolves the brand and categories a product request points at (they must exist and be active).</summary>
public sealed class ProductClassifier(IBrandRepository brands, ICategoryRepository categories)
{
    public async Task<(ProductDetails Details, Category? Primary)> BuildAsync(ProductDetailsRequest r, string slug, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        BrandRef? brandRef = null;
        if (r.BrandId is { } brandId)
        {
            var brand = await brands.GetByIdAsync(brandId, ct);
            if (brand is not { IsActive: true }) errors["brandId"] = ["Brand does not exist or is inactive."];
            else brandRef = new BrandRef { Id = brand.Id, Name = brand.Name, Slug = brand.Slug };
        }

        var ids = (r.CategoryIds ?? []).Distinct().ToList();
        if (r.PrimaryCategoryId is { } pid && !ids.Contains(pid)) ids.Add(pid);
        var found = ids.Count == 0 ? [] : await categories.GetByIdsAsync(ids, ct);
        foreach (var missing in ids.Where(id => !found.Any(c => c.Id == id && c.IsActive)))
            errors[r.PrimaryCategoryId == missing ? "primaryCategoryId" : "categoryIds"] = [$"Category {missing} does not exist or is inactive."];

        if (errors.Count > 0)
            throw new CatalogException(CatalogErrors.ValidationFailed, "One or more references are invalid.", FailureKind.Validation, errors);

        var primaryId = r.PrimaryCategoryId ?? ids.Cast<Guid?>().FirstOrDefault();
        var primary = primaryId is null ? null : found.First(c => c.Id == primaryId);

        var details = new ProductDetails
        {
            Name = r.Name,
            Slug = slug,
            Description = r.Description,
            Brand = brandRef,
            CategoryIds = ids,
            PrimaryCategoryId = primary?.Id,
            Attributes = ProductMapping.ToAttributeValues(r.Attributes),
            Tags = r.Tags ?? [],
            Seo = r.Seo is null ? null : new Seo { Title = r.Seo.Title, Description = r.Seo.Description },
        };
        return (details, primary);
    }
}

/// <summary>
/// Shared write path for product updates: load, check the caller's version (BR-08), mutate the aggregate, save with a
/// version-guarded replace, then invalidate the cache for the old and the new slug (AC-4).
/// </summary>
public sealed class ProductWriter(
    IProductRepository products, ICatalogCache cache, TimeProvider time, IOptions<CatalogOptions> options)
{
    public string Currency => options.Value.Currency;

    public async Task<AdminProductDto> UpdateAsync(
        Guid id, long expectedVersion, Func<Product, DateTime, CancellationToken, Task> mutate, CancellationToken ct, string? statusMetric = null)
    {
        var product = await products.GetByIdAsync(id, ct)
            ?? throw CatalogException.NotFound(CatalogErrors.ProductNotFound, "Product not found.");
        if (product.Version != expectedVersion) throw VersionMismatch();

        var oldSlug = product.Slug;
        await mutate(product, time.GetUtcNow().UtcDateTime, ct);
        await products.ReplaceAsync(product, expectedVersion, ct);
        await cache.RemoveAsync(Invalidations(oldSlug, product.Slug), ct);

        if (statusMetric is not null)
            CatalogMetrics.StatusChanges.Add(1, new KeyValuePair<string, object?>("to", statusMetric));
        return product.ToAdmin(Currency);
    }

    public static IEnumerable<string> Invalidations(params string[] slugs) =>
        slugs.Distinct().Select(CacheKeys.Product);

    public static CatalogException VersionMismatch() => new(CatalogErrors.VersionMismatch,
        "The product was changed by someone else. Reload it and try again.", FailureKind.PreconditionFailed);
}

public sealed class SlugService(IProductRepository products)
{
    /// <summary>BR-01: an explicit slug must be free; otherwise one is derived from the name, with -2, -3 ... on collision.</summary>
    public async Task<string> ResolveForCreateAsync(string name, string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return await EnsureFreeAsync(requested, null, ct);
        var baseSlug = Slugs.From(name);
        var taken = await products.SlugsStartingWithAsync(baseSlug, ct);
        return Slugs.NextAvailable(baseSlug, taken.Contains);
    }

    public async Task<string> EnsureFreeAsync(string slug, Guid? exceptId, CancellationToken ct)
    {
        if (!Slugs.IsValid(slug)) throw CatalogException.Invalid("slug", "Slug must be lowercase letters, digits and single hyphens.");
        if (await products.SlugExistsAsync(slug, exceptId, ct))
            throw CatalogException.Conflict(CatalogErrors.SlugTaken, $"The slug '{slug}' is already in use.");
        return slug;
    }
}
