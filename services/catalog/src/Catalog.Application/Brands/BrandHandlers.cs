using BuildingBlocks.Common;
using Catalog.Application.Abstractions;
using Catalog.Domain;
using MediatR;

namespace Catalog.Application.Brands;

public sealed record BrandDto(Guid Id, string Name, string Slug, string? LogoUrl);
public sealed record AdminBrandDto(Guid Id, string Name, string Slug, string? LogoUrl, bool IsActive, long Version, DateTime UpdatedAt);
public sealed record BrandRequest(string Name, string? Slug, string? LogoUrl, bool IsActive = true);

public static class BrandMapping
{
    public static BrandDto ToDto(this Brand b) => new(b.Id, b.Name, b.Slug, b.LogoUrl);
    public static AdminBrandDto ToAdmin(this Brand b) => new(b.Id, b.Name, b.Slug, b.LogoUrl, b.IsActive, b.Version, b.UpdatedAt);
}

public sealed record ListBrandsQuery : IRequest<IReadOnlyList<BrandDto>>;

public sealed class ListBrandsHandler(IBrandRepository brands) : IRequestHandler<ListBrandsQuery, IReadOnlyList<BrandDto>>
{
    public async Task<IReadOnlyList<BrandDto>> Handle(ListBrandsQuery q, CancellationToken ct) =>
        (await brands.ListAsync(activeOnly: true, ct)).Select(b => b.ToDto()).ToList();
}

public sealed record ListAdminBrandsQuery : IRequest<IReadOnlyList<AdminBrandDto>>;

public sealed class ListAdminBrandsHandler(IBrandRepository brands) : IRequestHandler<ListAdminBrandsQuery, IReadOnlyList<AdminBrandDto>>
{
    public async Task<IReadOnlyList<AdminBrandDto>> Handle(ListAdminBrandsQuery q, CancellationToken ct) =>
        (await brands.ListAsync(activeOnly: false, ct)).Select(b => b.ToAdmin()).ToList();
}

public sealed record GetAdminBrandQuery(Guid Id) : IRequest<AdminBrandDto>;

public sealed class GetAdminBrandHandler(IBrandRepository brands) : IRequestHandler<GetAdminBrandQuery, AdminBrandDto>
{
    public async Task<AdminBrandDto> Handle(GetAdminBrandQuery q, CancellationToken ct) =>
        (await brands.GetByIdAsync(q.Id, ct) ?? throw CatalogException.NotFound(CatalogErrors.BrandNotFound, "Brand not found.")).ToAdmin();
}

public sealed record CreateBrandCommand(BrandRequest Body) : IRequest<AdminBrandDto>;

public sealed class CreateBrandHandler(IBrandRepository brands, TimeProvider time) : IRequestHandler<CreateBrandCommand, AdminBrandDto>
{
    public async Task<AdminBrandDto> Handle(CreateBrandCommand c, CancellationToken ct)
    {
        string slug;
        if (!string.IsNullOrWhiteSpace(c.Body.Slug))
        {
            slug = c.Body.Slug;
            if (await brands.SlugExistsAsync(slug, null, ct)) throw CatalogException.Conflict(CatalogErrors.SlugTaken, $"The slug '{slug}' is already in use.");
        }
        else
        {
            var baseSlug = Slugs.From(c.Body.Name);
            var taken = await brands.SlugsStartingWithAsync(baseSlug, ct);
            slug = Slugs.NextAvailable(baseSlug, taken.Contains);
        }

        var brand = Brand.Create(Ids.NewGuidV7(), c.Body.Name, slug, c.Body.LogoUrl, c.Body.IsActive, time.GetUtcNow().UtcDateTime);
        await brands.InsertAsync(brand, ct);
        return brand.ToAdmin();
    }
}

public sealed record UpdateBrandCommand(Guid Id, long IfMatch, BrandRequest Body) : IRequest<AdminBrandDto>;

/// <summary>Updating a brand also refreshes the denormalised brand reference on its products and drops their cached pages.</summary>
public sealed class UpdateBrandHandler(IBrandRepository brands, IProductRepository products, ICatalogCache cache, TimeProvider time)
    : IRequestHandler<UpdateBrandCommand, AdminBrandDto>
{
    public async Task<AdminBrandDto> Handle(UpdateBrandCommand c, CancellationToken ct)
    {
        var brand = await brands.GetByIdAsync(c.Id, ct) ?? throw CatalogException.NotFound(CatalogErrors.BrandNotFound, "Brand not found.");
        if (brand.Version != c.IfMatch) throw Categories.UpdateCategoryHandler.Mismatch();

        var slug = string.IsNullOrWhiteSpace(c.Body.Slug) ? brand.Slug : c.Body.Slug;
        if (slug != brand.Slug && await brands.SlugExistsAsync(slug, brand.Id, ct))
            throw CatalogException.Conflict(CatalogErrors.SlugTaken, $"The slug '{slug}' is already in use.");

        brand.Update(c.Body.Name, slug, c.Body.LogoUrl, c.Body.IsActive, time.GetUtcNow().UtcDateTime);
        await brands.ReplaceAsync(brand, c.IfMatch, ct);

        var affected = await products.UpdateBrandRefAsync(new BrandRef { Id = brand.Id, Name = brand.Name, Slug = brand.Slug }, ct);
        await cache.RemoveAsync(affected.Select(CacheKeys.Product), ct);
        return brand.ToAdmin();
    }
}

public sealed record DeleteBrandCommand(Guid Id) : IRequest;

public sealed class DeleteBrandHandler(IBrandRepository brands, IProductRepository products) : IRequestHandler<DeleteBrandCommand>
{
    public async Task Handle(DeleteBrandCommand c, CancellationToken ct)
    {
        _ = await brands.GetByIdAsync(c.Id, ct) ?? throw CatalogException.NotFound(CatalogErrors.BrandNotFound, "Brand not found.");
        if (await products.AnyWithBrandAsync(c.Id, ct))
            throw CatalogException.Conflict(CatalogErrors.BrandInUse, "Products still use this brand. Deactivate it instead.");
        await brands.DeleteAsync(c.Id, ct);
    }
}
