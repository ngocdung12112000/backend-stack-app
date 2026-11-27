using BuildingBlocks.Common;
using BuildingBlocks.Security;
using Catalog.Application.Brands;
using Catalog.Application.Categories;
using Catalog.Application.Products;
using Catalog.Domain;
using MediatR;

namespace Catalog.Api.Endpoints;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        MapPublic(app.MapGroup("/api/v1"));
        var admin = app.MapGroup("/api/v1/admin").RequireAuthorization(Permissions.Policy("catalog:write"));
        MapAdminProducts(admin.MapGroup("/products").WithTags("Admin products"));
        MapAdminCategories(admin.MapGroup("/categories").WithTags("Admin categories"));
        MapAdminBrands(admin.MapGroup("/brands").WithTags("Admin brands"));
    }

    // ---------------------------------------------------------------- public

    private static void MapPublic(RouteGroupBuilder api)
    {
        api.MapGet("/products", async (ISender s, CancellationToken ct, int page = 1, int pageSize = 20,
                string? category = null, string? brand = null, string? sort = null, string? ids = null) =>
                Results.Ok(await s.Send(new ListProductsQuery(page, pageSize, category, brand, sort, ParseIds(ids)), ct)))
            .WithName("ListProducts")
            .WithSummary("Published products. Filter by category/brand slug, sort by newest|price_asc|price_desc|name, or fetch a batch with ids=a,b,c (max 50, input order).")
            .Produces<PagedResult<ProductDto>>().ProducesProblem(StatusCodes.Status400BadRequest)
            .WithTags("Products");

        api.MapGet("/products/{slug}", async (string slug, ISender s, CancellationToken ct) =>
                Results.Ok(await s.Send(new GetProductBySlugQuery(slug), ct)))
            .WithName("GetProduct")
            .WithSummary("Published product detail (Redis cache-aside). Draft and archived products are 404.")
            .Produces<ProductDetailDto>().ProducesProblem(StatusCodes.Status404NotFound)
            .WithTags("Products");

        api.MapGet("/categories/tree", async (ISender s, CancellationToken ct) => Results.Ok(await s.Send(new GetCategoryTreeQuery(), ct)))
            .WithName("GetCategoryTree").WithSummary("Active category tree for the navigation menu (cached).")
            .Produces<IReadOnlyList<CategoryNodeDto>>().WithTags("Categories");

        api.MapGet("/categories/{slug}", async (string slug, ISender s, CancellationToken ct) => Results.Ok(await s.Send(new GetCategoryBySlugQuery(slug), ct)))
            .WithName("GetCategory").WithSummary("Category with its attribute schema (filterable attributes drive the facets).")
            .Produces<CategoryDto>().ProducesProblem(StatusCodes.Status404NotFound).WithTags("Categories");

        api.MapGet("/brands", async (ISender s, CancellationToken ct) => Results.Ok(await s.Send(new ListBrandsQuery(), ct)))
            .WithName("ListBrands").WithSummary("Active brands.")
            .Produces<IReadOnlyList<BrandDto>>().WithTags("Brands");
    }

    private static IReadOnlyList<Guid>? ParseIds(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids)) return null;
        var parts = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<Guid>(parts.Length);
        foreach (var part in parts)
        {
            if (!Guid.TryParse(part, out var id)) throw CatalogException.Invalid("ids", $"'{part}' is not a valid product id.");
            list.Add(id);
        }

        return list;
    }

    // ---------------------------------------------------------------- admin: products

    private static void MapAdminProducts(RouteGroupBuilder g)
    {
        g.MapGet("/", async (ISender s, CancellationToken ct, ProductStatus? status = null, string? q = null, string? category = null, int page = 1, int pageSize = 20) =>
                Results.Ok(await s.Send(new ListAdminProductsQuery(status, q, category, page, pageSize), ct)))
            .WithName("AdminListProducts").WithSummary("Paged product list incl. drafts and archived.")
            .Produces<PagedResult<AdminProductListItemDto>>().AdminErrors();

        g.MapGet("/{id:guid}", async (Guid id, ISender s, HttpContext ctx, CancellationToken ct) =>
                Etagged(ctx, await s.Send(new GetAdminProductQuery(id), ct)))
            .WithName("AdminGetProduct").WithSummary("Full product incl. drafts. The ETag is the version to send back in If-Match.")
            .Produces<AdminProductDto>().AdminErrors();

        g.MapPost("/", async (ProductDetailsRequest body, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var created = await s.Send(new CreateProductCommand(body), ct);
                ctx.Response.Headers.ETag = ETags.For(created.Version);
                return Results.Created($"/api/v1/admin/products/{created.Id}", created);
            })
            .WithName("AdminCreateProduct").WithSummary("Create a draft. The slug is generated from the name unless given.")
            .Produces<AdminProductDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict).AdminErrors();

        g.MapPut("/{id:guid}", async (Guid id, ProductDetailsRequest body, ISender s, HttpContext ctx, CancellationToken ct) =>
                Etagged(ctx, await s.Send(new UpdateProductCommand(id, ETags.RequireIfMatch(ctx.Request), body), ct)))
            .WithName("AdminUpdateProduct").WithSummary("Replace details and attributes (If-Match required, 412 on a stale version).")
            .Produces<AdminProductDto>().ProducesProblem(StatusCodes.Status409Conflict).AdminErrors(concurrent: true);

        g.MapPut("/{id:guid}/options", async (Guid id, SetOptionsRequest body, ISender s, HttpContext ctx, CancellationToken ct) =>
                Etagged(ctx, await s.Send(new SetOptionsCommand(id, ETags.RequireIfMatch(ctx.Request), body), ct)))
            .WithName("AdminSetProductOptions").WithSummary("Set the variant options (max 3 options, 20 values each).")
            .Produces<AdminProductDto>().ProducesProblem(StatusCodes.Status409Conflict).AdminErrors(concurrent: true);

        g.MapPut("/{id:guid}/variants/{sku}", async (Guid id, string sku, VariantRequest body, ISender s, HttpContext ctx, CancellationToken ct) =>
                Etagged(ctx, await s.Send(new UpsertVariantCommand(id, sku, ETags.RequireIfMatch(ctx.Request), body), ct)))
            .WithName("AdminUpsertVariant").WithSummary("Create or replace a variant. SKUs are unique across all products (409).")
            .Produces<AdminProductDto>().ProducesProblem(StatusCodes.Status409Conflict).AdminErrors(concurrent: true);

        g.MapDelete("/{id:guid}/variants/{sku}", async (Guid id, string sku, ISender s, HttpContext ctx, CancellationToken ct) =>
                Etagged(ctx, await s.Send(new RemoveVariantCommand(id, sku, ETags.RequireIfMatch(ctx.Request)), ct)))
            .WithName("AdminRemoveVariant").WithSummary("Remove a variant (stock check arrives with the inventory service).")
            .Produces<AdminProductDto>().ProducesProblem(StatusCodes.Status409Conflict).AdminErrors(concurrent: true);

        g.MapPut("/{id:guid}/images", async (Guid id, SetImagesRequest body, ISender s, HttpContext ctx, CancellationToken ct) =>
                Etagged(ctx, await s.Send(new SetImagesCommand(id, ETags.RequireIfMatch(ctx.Request), body), ct)))
            .WithName("AdminSetProductImages").WithSummary("Ordered image list (absolute URLs until the media service exists).")
            .Produces<AdminProductDto>().AdminErrors(concurrent: true);

        MapStatus(g, "publish", ProductAction.Publish, "Publish a draft (BR-05 requirements; 422 CATALOG_PUBLISH_REQUIREMENTS_NOT_MET).");
        MapStatus(g, "unpublish", ProductAction.Unpublish, "Return a published product to draft.");
        MapStatus(g, "archive", ProductAction.Archive, "Archive: hidden from the storefront, still resolvable by id for past orders.");
    }

    private static void MapStatus(RouteGroupBuilder g, string route, ProductAction action, string summary) =>
        g.MapPost($"/{{id:guid}}/{route}", async (Guid id, ISender s, HttpContext ctx, CancellationToken ct) =>
                Etagged(ctx, await s.Send(new ChangeProductStatusCommand(id, ETags.RequireIfMatch(ctx.Request), action), ct)))
            .WithName($"Admin{action}Product").WithSummary(summary)
            .Produces<AdminProductDto>().ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity).AdminErrors(concurrent: true);

    // ---------------------------------------------------------------- admin: categories

    private static void MapAdminCategories(RouteGroupBuilder g)
    {
        g.MapGet("/", async (ISender s, CancellationToken ct) => Results.Ok(await s.Send(new ListAdminCategoriesQuery(), ct)))
            .WithName("AdminListCategories").WithSummary("All categories (flat, incl. inactive); build the tree from parentId.")
            .Produces<IReadOnlyList<AdminCategoryDto>>().AdminErrors();

        g.MapGet("/{id:guid}", async (Guid id, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var c = await s.Send(new GetAdminCategoryQuery(id), ct);
                return Etagged(ctx, c, c.Version);
            })
            .WithName("AdminGetCategory").Produces<AdminCategoryDto>().AdminErrors();

        g.MapPost("/", async (CategoryRequest body, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var c = await s.Send(new CreateCategoryCommand(body), ct);
                ctx.Response.Headers.ETag = ETags.For(c.Version);
                return Results.Created($"/api/v1/admin/categories/{c.Id}", c);
            })
            .WithName("AdminCreateCategory").WithSummary("Create a category (max 3 levels).")
            .Produces<AdminCategoryDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict).AdminErrors();

        g.MapPut("/{id:guid}", async (Guid id, CategoryRequest body, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var c = await s.Send(new UpdateCategoryCommand(id, ETags.RequireIfMatch(ctx.Request), body), ct);
                return Etagged(ctx, c, c.Version);
            })
            .WithName("AdminUpdateCategory").WithSummary("Update name, order, active flag and attribute schema (If-Match). Slug and parent are fixed.")
            .Produces<AdminCategoryDto>().AdminErrors(concurrent: true);

        g.MapDelete("/{id:guid}", async (Guid id, ISender s, CancellationToken ct) =>
            {
                await s.Send(new DeleteCategoryCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteCategory").WithSummary("Delete an unused leaf category (BR-09: otherwise deactivate it).")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict).AdminErrors();
    }

    // ---------------------------------------------------------------- admin: brands

    private static void MapAdminBrands(RouteGroupBuilder g)
    {
        g.MapGet("/", async (ISender s, CancellationToken ct) => Results.Ok(await s.Send(new ListAdminBrandsQuery(), ct)))
            .WithName("AdminListBrands").Produces<IReadOnlyList<AdminBrandDto>>().AdminErrors();

        g.MapGet("/{id:guid}", async (Guid id, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var b = await s.Send(new GetAdminBrandQuery(id), ct);
                return Etagged(ctx, b, b.Version);
            })
            .WithName("AdminGetBrand").Produces<AdminBrandDto>().AdminErrors();

        g.MapPost("/", async (BrandRequest body, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var b = await s.Send(new CreateBrandCommand(body), ct);
                ctx.Response.Headers.ETag = ETags.For(b.Version);
                return Results.Created($"/api/v1/admin/brands/{b.Id}", b);
            })
            .WithName("AdminCreateBrand").Produces<AdminBrandDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict).AdminErrors();

        g.MapPut("/{id:guid}", async (Guid id, BrandRequest body, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var b = await s.Send(new UpdateBrandCommand(id, ETags.RequireIfMatch(ctx.Request), body), ct);
                return Etagged(ctx, b, b.Version);
            })
            .WithName("AdminUpdateBrand").Produces<AdminBrandDto>().ProducesProblem(StatusCodes.Status409Conflict).AdminErrors(concurrent: true);

        g.MapDelete("/{id:guid}", async (Guid id, ISender s, CancellationToken ct) =>
            {
                await s.Send(new DeleteBrandCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteBrand").WithSummary("Delete an unused brand (otherwise deactivate it).")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict).AdminErrors();
    }

    // ---------------------------------------------------------------- helpers

    private static IResult Etagged(HttpContext ctx, AdminProductDto dto) => Etagged(ctx, dto, dto.Version);

    private static IResult Etagged<T>(HttpContext ctx, T dto, long version)
    {
        ctx.Response.Headers.ETag = ETags.For(version);
        return Results.Ok(dto);
    }

    private static RouteHandlerBuilder AdminErrors(this RouteHandlerBuilder b, bool concurrent = false)
    {
        b.ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        return concurrent
            ? b.ProducesProblem(StatusCodes.Status412PreconditionFailed).ProducesProblem(StatusCodes.Status428PreconditionRequired)
            : b;
    }
}
