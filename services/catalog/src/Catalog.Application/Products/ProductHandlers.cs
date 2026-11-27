using BuildingBlocks.Common;
using Catalog.Application.Abstractions;
using Catalog.Domain;
using MediatR;
using Microsoft.Extensions.Options;

namespace Catalog.Application.Products;

// ---------- Public (storefront) ----------

/// <summary>Published products. <c>Ids</c> switches to batch mode (input order, max 50); otherwise page, category, brand and sort apply.</summary>
public sealed record ListProductsQuery(
    int Page = 1, int PageSize = 20, string? Category = null, string? Brand = null, string? Sort = null, IReadOnlyList<Guid>? Ids = null)
    : IRequest<PagedResult<ProductDto>>;

public sealed class ListProductsHandler(IProductRepository products, ICategoryRepository categories, IOptions<CatalogOptions> options)
    : IRequestHandler<ListProductsQuery, PagedResult<ProductDto>>
{
    public const int MaxBatchIds = 50;

    public async Task<PagedResult<ProductDto>> Handle(ListProductsQuery q, CancellationToken ct)
    {
        var currency = options.Value.Currency;
        if (q.Ids is { Count: > 0 } ids)
        {
            var batch = await products.GetPublishedByIdsAsync(ids.Distinct().Take(MaxBatchIds).ToList(), ct);
            return new PagedResult<ProductDto>(batch.Select(p => p.ToCard(currency)).ToList(), 1, MaxBatchIds, batch.Count);
        }

        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 100);

        IReadOnlyList<Guid>? categoryIds = null;
        if (!string.IsNullOrWhiteSpace(q.Category))
        {
            categoryIds = await categories.GetSelfAndDescendantIdsAsync(q.Category.Trim().ToLowerInvariant(), ct);
            if (categoryIds.Count == 0) return PagedResult<ProductDto>.Empty(page, size);
        }

        var sort = q.Sort?.ToLowerInvariant() switch
        {
            "price_asc" or "price-asc" => ProductSort.PriceAsc,
            "price_desc" or "price-desc" => ProductSort.PriceDesc,
            "name" => ProductSort.Name,
            _ => ProductSort.Newest,
        };

        var (items, total) = await products.QueryAsync(
            new ProductQuery(ProductStatus.Published, categoryIds, string.IsNullOrWhiteSpace(q.Brand) ? null : q.Brand.Trim().ToLowerInvariant(), null, sort, page, size), ct);
        return new PagedResult<ProductDto>(items.Select(p => p.ToCard(currency)).ToList(), page, size, total);
    }
}

/// <summary>Published product by slug, cache-aside (<c>cache:product:{slug}</c>). Archived, draft and unknown slugs are all 404 (BR-07, AC-6).</summary>
public sealed record GetProductBySlugQuery(string Slug) : IRequest<ProductDetailDto>;

public sealed class GetProductBySlugHandler(IProductRepository products, ICatalogCache cache, IOptions<CatalogOptions> options)
    : IRequestHandler<GetProductBySlugQuery, ProductDetailDto>
{
    public async Task<ProductDetailDto> Handle(GetProductBySlugQuery q, CancellationToken ct)
    {
        var slug = q.Slug.Trim().ToLowerInvariant();
        var o = options.Value;
        var ttl = TimeSpan.FromMinutes(o.ProductCacheMinutes) + TimeSpan.FromSeconds(Random.Shared.Next(0, Math.Max(1, o.ProductCacheJitterSeconds)));

        var dto = await cache.GetOrCreateAsync<ProductDetailDto>(CacheKeys.Product(slug), ttl, async token =>
        {
            var product = await products.GetPublishedBySlugAsync(slug, token);
            return product?.ToDetail(o.Currency);
        }, ct);

        return dto ?? throw CatalogException.NotFound(CatalogErrors.ProductNotFound, "Product not found.");
    }
}

// ---------- Admin ----------

public sealed record ListAdminProductsQuery(ProductStatus? Status, string? Q, string? Category, int Page = 1, int PageSize = 20)
    : IRequest<PagedResult<AdminProductListItemDto>>;

public sealed class ListAdminProductsHandler(IProductRepository products, ICategoryRepository categories)
    : IRequestHandler<ListAdminProductsQuery, PagedResult<AdminProductListItemDto>>
{
    public async Task<PagedResult<AdminProductListItemDto>> Handle(ListAdminProductsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 100);

        IReadOnlyList<Guid>? categoryIds = null;
        if (!string.IsNullOrWhiteSpace(q.Category))
        {
            categoryIds = await categories.GetSelfAndDescendantIdsAsync(q.Category.Trim().ToLowerInvariant(), ct);
            if (categoryIds.Count == 0) return PagedResult<AdminProductListItemDto>.Empty(page, size);
        }

        var (items, total) = await products.QueryAsync(
            new ProductQuery(q.Status, categoryIds, null, string.IsNullOrWhiteSpace(q.Q) ? null : q.Q.Trim(), ProductSort.Newest, page, size), ct);
        return new PagedResult<AdminProductListItemDto>(items.Select(p => p.ToListItem()).ToList(), page, size, total);
    }
}

/// <summary>Full product including drafts and archived. Not cached.</summary>
public sealed record GetAdminProductQuery(Guid Id) : IRequest<AdminProductDto>;

public sealed class GetAdminProductHandler(IProductRepository products, IOptions<CatalogOptions> options)
    : IRequestHandler<GetAdminProductQuery, AdminProductDto>
{
    public async Task<AdminProductDto> Handle(GetAdminProductQuery q, CancellationToken ct) =>
        (await products.GetByIdAsync(q.Id, ct) ?? throw CatalogException.NotFound(CatalogErrors.ProductNotFound, "Product not found."))
        .ToAdmin(options.Value.Currency);
}

public sealed record CreateProductCommand(ProductDetailsRequest Details) : IRequest<AdminProductDto>;

public sealed class CreateProductHandler(
    IProductRepository products, ProductClassifier classifier, SlugService slugs, ICatalogCache cache, TimeProvider time, IOptions<CatalogOptions> options)
    : IRequestHandler<CreateProductCommand, AdminProductDto>
{
    public async Task<AdminProductDto> Handle(CreateProductCommand c, CancellationToken ct)
    {
        var slug = await slugs.ResolveForCreateAsync(c.Details.Name, c.Details.Slug, ct);
        var (details, primary) = await classifier.BuildAsync(c.Details, slug, ct);
        var product = Product.Create(Ids.NewGuidV7(), details, primary, time.GetUtcNow().UtcDateTime);

        await products.InsertAsync(product, ct);
        await cache.RemoveAsync([CacheKeys.Product(product.Slug)], ct); // clears any stale negative state for a reused slug
        return product.ToAdmin(options.Value.Currency);
    }
}

public sealed record UpdateProductCommand(Guid Id, long IfMatch, ProductDetailsRequest Details) : IRequest<AdminProductDto>;

public sealed class UpdateProductHandler(ProductWriter writer, ProductClassifier classifier, SlugService slugs)
    : IRequestHandler<UpdateProductCommand, AdminProductDto>
{
    public Task<AdminProductDto> Handle(UpdateProductCommand c, CancellationToken ct) =>
        writer.UpdateAsync(c.Id, c.IfMatch, async (product, now, token) =>
        {
            var slug = string.IsNullOrWhiteSpace(c.Details.Slug) ? product.Slug : c.Details.Slug;
            if (slug != product.Slug) await slugs.EnsureFreeAsync(slug, product.Id, token);
            var (details, primary) = await classifier.BuildAsync(c.Details, slug, token);
            product.UpdateDetails(details, primary, now);
        }, ct);
}

public sealed record SetOptionsCommand(Guid Id, long IfMatch, SetOptionsRequest Body) : IRequest<AdminProductDto>;

public sealed class SetOptionsHandler(ProductWriter writer) : IRequestHandler<SetOptionsCommand, AdminProductDto>
{
    public Task<AdminProductDto> Handle(SetOptionsCommand c, CancellationToken ct) =>
        writer.UpdateAsync(c.Id, c.IfMatch, (product, now, _) =>
        {
            product.SetOptions(c.Body.Options.Select(o => new ProductOption { Name = o.Name ?? "", Values = o.Values ?? [] }).ToList(), now);
            return Task.CompletedTask;
        }, ct);
}

public sealed record UpsertVariantCommand(Guid Id, string Sku, long IfMatch, VariantRequest Body) : IRequest<AdminProductDto>;

public sealed class UpsertVariantHandler(ProductWriter writer, IProductRepository products) : IRequestHandler<UpsertVariantCommand, AdminProductDto>
{
    public Task<AdminProductDto> Handle(UpsertVariantCommand c, CancellationToken ct) =>
        writer.UpdateAsync(c.Id, c.IfMatch, async (product, now, token) =>
        {
            var sku = Product.NormalizeSku(c.Sku);
            // AC-2: a SKU is unique across all products. The unique index is the final guard against races.
            var owner = await products.FindProductIdBySkuAsync(sku, token);
            if (owner is not null && owner != product.Id)
                throw CatalogException.Conflict(CatalogErrors.SkuTaken, $"The SKU '{sku}' is already used by another product.");

            product.UpsertVariant(new VariantInput
            {
                Sku = sku,
                Options = new Dictionary<string, string>(c.Body.Options ?? new Dictionary<string, string>()),
                Price = c.Body.Price,
                CompareAtPrice = c.Body.CompareAtPrice,
                WeightG = c.Body.WeightG,
                Barcode = c.Body.Barcode,
                IsActive = c.Body.IsActive,
            }, now);
        }, ct);
}

public sealed record RemoveVariantCommand(Guid Id, string Sku, long IfMatch) : IRequest<AdminProductDto>;

public sealed class RemoveVariantHandler(ProductWriter writer) : IRequestHandler<RemoveVariantCommand, AdminProductDto>
{
    public Task<AdminProductDto> Handle(RemoveVariantCommand c, CancellationToken ct) =>
        writer.UpdateAsync(c.Id, c.IfMatch, (product, now, _) =>
        {
            // TODO(F05): ask inventory (gRPC CheckStock) and fail with CATALOG_VARIANT_HAS_STOCK while stock exists.
            product.RemoveVariant(c.Sku, now);
            return Task.CompletedTask;
        }, ct);
}

public sealed record SetImagesCommand(Guid Id, long IfMatch, SetImagesRequest Body) : IRequest<AdminProductDto>;

public sealed class SetImagesHandler(ProductWriter writer) : IRequestHandler<SetImagesCommand, AdminProductDto>
{
    public Task<AdminProductDto> Handle(SetImagesCommand c, CancellationToken ct) =>
        writer.UpdateAsync(c.Id, c.IfMatch, (product, now, _) =>
        {
            // Plain URLs until F14 (media) provides asset ids.
            product.SetImages(c.Body.Images.Select(i => new ImageInput { Url = i.Url, ThumbUrl = i.ThumbUrl, Alt = i.Alt }).ToList(), now);
            return Task.CompletedTask;
        }, ct);
}

public enum ProductAction { Publish, Unpublish, Archive }

public sealed record ChangeProductStatusCommand(Guid Id, long IfMatch, ProductAction Action) : IRequest<AdminProductDto>;

public sealed class ChangeProductStatusHandler(ProductWriter writer) : IRequestHandler<ChangeProductStatusCommand, AdminProductDto>
{
    public Task<AdminProductDto> Handle(ChangeProductStatusCommand c, CancellationToken ct) =>
        writer.UpdateAsync(c.Id, c.IfMatch, (product, now, _) =>
        {
            switch (c.Action)
            {
                case ProductAction.Publish: product.Publish(now); break;
                case ProductAction.Unpublish: product.Unpublish(now); break;
                default: product.Archive(now); break;
            }

            return Task.CompletedTask;
        }, ct, statusMetric: c.Action.ToString().ToLowerInvariant());
}
