using BuildingBlocks.Common;
using Catalog.Application.Abstractions;
using Catalog.Domain;
using MediatR;
using Microsoft.Extensions.Options;

namespace Catalog.Application.Categories;

public sealed record AttributeDefinitionDto(
    string Key, string Label, string Type, string? Unit, IReadOnlyList<string>? Options, bool Filterable, bool Required);

public sealed record CategoryNodeDto(Guid Id, string Name, string Slug, int Level, IReadOnlyList<CategoryNodeDto> Children);

/// <summary>Storefront category with its attribute schema (the frontend builds facets from the filterable attributes).</summary>
public sealed record CategoryDto(
    Guid Id, string Name, string Slug, Guid? ParentId, IReadOnlyList<string> Path, int Level,
    IReadOnlyList<AttributeDefinitionDto> AttributeSchema);

public sealed record AdminCategoryDto(
    Guid Id, string Name, string Slug, Guid? ParentId, IReadOnlyList<string> Path, int Level, int Sort, bool IsActive,
    IReadOnlyList<AttributeDefinitionDto> AttributeSchema, long Version, DateTime UpdatedAt);

/// <summary><c>Slug</c> and <c>ParentId</c> are only used on create (a category keeps its place and slug).</summary>
public sealed record CategoryRequest(
    string Name, string? Slug, Guid? ParentId, int Sort, bool IsActive = true, IReadOnlyList<AttributeDefinitionDto>? AttributeSchema = null);

public static class CategoryMapping
{
    public static AttributeDefinitionDto ToDto(this AttributeDefinition a) =>
        new(a.Key, a.Label, a.Type, a.Unit, a.Options, a.Filterable, a.Required);

    public static AttributeDefinition ToDomain(this AttributeDefinitionDto a) => new()
    {
        Key = a.Key,
        Label = a.Label ?? "",
        Type = a.Type ?? "",
        Unit = a.Unit,
        Options = a.Options,
        Filterable = a.Filterable,
        Required = a.Required,
    };

    public static CategoryDto ToDto(this Category c) =>
        new(c.Id, c.Name, c.Slug, c.ParentId, c.Path, c.Level, c.AttributeSchema.Select(ToDto).ToList());

    public static AdminCategoryDto ToAdmin(this Category c) =>
        new(c.Id, c.Name, c.Slug, c.ParentId, c.Path, c.Level, c.Sort, c.IsActive, c.AttributeSchema.Select(ToDto).ToList(), c.Version, c.UpdatedAt);
}

/// <summary>Full active tree. A category under an inactive parent is hidden with it. Cached as <c>cache:category-tree</c>.</summary>
public sealed record GetCategoryTreeQuery : IRequest<IReadOnlyList<CategoryNodeDto>>;

public sealed class GetCategoryTreeHandler(ICategoryRepository categories, ICatalogCache cache, IOptions<CatalogOptions> options)
    : IRequestHandler<GetCategoryTreeQuery, IReadOnlyList<CategoryNodeDto>>
{
    public async Task<IReadOnlyList<CategoryNodeDto>> Handle(GetCategoryTreeQuery q, CancellationToken ct)
    {
        var tree = await cache.GetOrCreateAsync<CategoryTree>(CacheKeys.CategoryTree,
            TimeSpan.FromMinutes(options.Value.CategoryTreeCacheMinutes), async token =>
            {
                var all = await categories.ListAsync(activeOnly: true, token);
                return new CategoryTree(Build(all, null));
            }, ct);
        return tree!.Roots;
    }

    // Wrapper so an empty tree is still a cacheable (non-null) value.
    public sealed record CategoryTree(IReadOnlyList<CategoryNodeDto> Roots);

    private static List<CategoryNodeDto> Build(IReadOnlyList<Category> all, Guid? parentId) =>
        all.Where(c => c.ParentId == parentId).OrderBy(c => c.Sort).ThenBy(c => c.Name)
            .Select(c => new CategoryNodeDto(c.Id, c.Name, c.Slug, c.Level, Build(all, c.Id))).ToList();
}

public sealed record GetCategoryBySlugQuery(string Slug) : IRequest<CategoryDto>;

public sealed class GetCategoryBySlugHandler(ICategoryRepository categories) : IRequestHandler<GetCategoryBySlugQuery, CategoryDto>
{
    public async Task<CategoryDto> Handle(GetCategoryBySlugQuery q, CancellationToken ct)
    {
        var category = await categories.GetBySlugAsync(q.Slug.Trim().ToLowerInvariant(), ct);
        if (category is not { IsActive: true }) throw CatalogException.NotFound(CatalogErrors.CategoryNotFound, "Category not found.");
        return category.ToDto();
    }
}

public sealed record ListAdminCategoriesQuery : IRequest<IReadOnlyList<AdminCategoryDto>>;

public sealed class ListAdminCategoriesHandler(ICategoryRepository categories) : IRequestHandler<ListAdminCategoriesQuery, IReadOnlyList<AdminCategoryDto>>
{
    public async Task<IReadOnlyList<AdminCategoryDto>> Handle(ListAdminCategoriesQuery q, CancellationToken ct) =>
        (await categories.ListAsync(activeOnly: false, ct)).OrderBy(c => c.Path.Count).ThenBy(c => c.Sort).Select(c => c.ToAdmin()).ToList();
}

public sealed record GetAdminCategoryQuery(Guid Id) : IRequest<AdminCategoryDto>;

public sealed class GetAdminCategoryHandler(ICategoryRepository categories) : IRequestHandler<GetAdminCategoryQuery, AdminCategoryDto>
{
    public async Task<AdminCategoryDto> Handle(GetAdminCategoryQuery q, CancellationToken ct) =>
        (await categories.GetByIdAsync(q.Id, ct) ?? throw CatalogException.NotFound(CatalogErrors.CategoryNotFound, "Category not found.")).ToAdmin();
}

public sealed record CreateCategoryCommand(CategoryRequest Body) : IRequest<AdminCategoryDto>;

public sealed class CreateCategoryHandler(ICategoryRepository categories, ICatalogCache cache, TimeProvider time)
    : IRequestHandler<CreateCategoryCommand, AdminCategoryDto>
{
    public async Task<AdminCategoryDto> Handle(CreateCategoryCommand c, CancellationToken ct)
    {
        var b = c.Body;
        Category? parent = null;
        if (b.ParentId is { } parentId)
            parent = await categories.GetByIdAsync(parentId, ct)
                ?? throw new CatalogException(CatalogErrors.CategoryNotFound, "Parent category not found.", FailureKind.Validation,
                    new Dictionary<string, string[]> { ["parentId"] = ["Parent category does not exist."] });

        string slug;
        if (!string.IsNullOrWhiteSpace(b.Slug))
        {
            slug = b.Slug;
            if (await categories.SlugExistsAsync(slug, ct)) throw CatalogException.Conflict(CatalogErrors.SlugTaken, $"The slug '{slug}' is already in use.");
        }
        else
        {
            var baseSlug = Slugs.From(b.Name);
            var taken = await categories.SlugsStartingWithAsync(baseSlug, ct);
            slug = Slugs.NextAvailable(baseSlug, taken.Contains);
        }

        var category = Category.Create(Ids.NewGuidV7(), b.Name, slug, parent, b.Sort,
            b.AttributeSchema?.Select(a => a.ToDomain()).ToList(), time.GetUtcNow().UtcDateTime);
        if (!b.IsActive) category.Update(category.Name, category.Sort, false, category.AttributeSchema, time.GetUtcNow().UtcDateTime);

        await categories.InsertAsync(category, ct);
        await cache.RemoveAsync([CacheKeys.CategoryTree], ct);
        return category.ToAdmin();
    }
}

public sealed record UpdateCategoryCommand(Guid Id, long IfMatch, CategoryRequest Body) : IRequest<AdminCategoryDto>;

public sealed class UpdateCategoryHandler(ICategoryRepository categories, ICatalogCache cache, TimeProvider time)
    : IRequestHandler<UpdateCategoryCommand, AdminCategoryDto>
{
    public async Task<AdminCategoryDto> Handle(UpdateCategoryCommand c, CancellationToken ct)
    {
        var category = await categories.GetByIdAsync(c.Id, ct) ?? throw CatalogException.NotFound(CatalogErrors.CategoryNotFound, "Category not found.");
        if (category.Version != c.IfMatch) throw Mismatch();

        category.Update(c.Body.Name, c.Body.Sort, c.Body.IsActive, c.Body.AttributeSchema?.Select(a => a.ToDomain()).ToList(), time.GetUtcNow().UtcDateTime);
        await categories.ReplaceAsync(category, c.IfMatch, ct);
        await cache.RemoveAsync([CacheKeys.CategoryTree], ct);
        return category.ToAdmin();
    }

    internal static CatalogException Mismatch() => new(CatalogErrors.VersionMismatch,
        "The record was changed by someone else. Reload it and try again.", FailureKind.PreconditionFailed);
}

/// <summary>BR-09: a category in use by products (or with sub-categories) cannot be deleted, only deactivated.</summary>
public sealed record DeleteCategoryCommand(Guid Id) : IRequest;

public sealed class DeleteCategoryHandler(ICategoryRepository categories, IProductRepository products, ICatalogCache cache)
    : IRequestHandler<DeleteCategoryCommand>
{
    public async Task Handle(DeleteCategoryCommand c, CancellationToken ct)
    {
        _ = await categories.GetByIdAsync(c.Id, ct) ?? throw CatalogException.NotFound(CatalogErrors.CategoryNotFound, "Category not found.");
        if (await categories.HasChildrenAsync(c.Id, ct))
            throw CatalogException.Conflict(CatalogErrors.CategoryHasChildren, "The category has sub-categories. Delete or move them first.");
        if (await products.AnyInCategoryAsync(c.Id, ct))
            throw CatalogException.Conflict(CatalogErrors.CategoryHasProducts, "Products still use this category. Deactivate it instead.");

        await categories.DeleteAsync(c.Id, ct);
        await cache.RemoveAsync([CacheKeys.CategoryTree], ct);
    }
}
