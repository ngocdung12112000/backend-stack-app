using Catalog.Application;
using Catalog.Application.Abstractions;
using Catalog.Application.Brands;
using Catalog.Application.Categories;
using Catalog.Application.Products;
using Catalog.Domain;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Catalog.Tests;

/// <summary>In-memory stand-ins for the Mongo repositories that behave like the real unique indexes and version guard.</summary>
internal sealed class FakeStore : IProductRepository, ICategoryRepository, IBrandRepository
{
    public Dictionary<Guid, Product> Products { get; } = [];
    public Dictionary<Guid, Category> Categories { get; } = [];
    public Dictionary<Guid, Brand> Brands { get; } = [];

    // products
    public Task<Product?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Products.GetValueOrDefault(id));
    public Task<Product?> GetPublishedBySlugAsync(string slug, CancellationToken ct) =>
        Task.FromResult(Products.Values.FirstOrDefault(p => p.Slug == slug && p.Status == ProductStatus.Published));
    public Task<IReadOnlyList<Product>> GetPublishedByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Product>>(ids.Where(Products.ContainsKey).Select(i => Products[i]).Where(p => p.Status == ProductStatus.Published).ToList());
    public Task<(IReadOnlyList<Product> Items, long Total)> QueryAsync(ProductQuery q, CancellationToken ct)
    {
        var all = Products.Values.Where(p => q.Status is null || p.Status == q.Status).ToList();
        return Task.FromResult<(IReadOnlyList<Product>, long)>((all.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToList(), all.Count));
    }
    public Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct) =>
        Task.FromResult(Products.Values.Any(p => p.Slug == slug && p.Id != exceptId));
    Task<IReadOnlySet<string>> IProductRepository.SlugsStartingWithAsync(string prefix, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(Products.Values.Select(p => p.Slug).Where(s => s.StartsWith(prefix)).ToHashSet());
    public Task<Guid?> FindProductIdBySkuAsync(string sku, CancellationToken ct) =>
        Task.FromResult(Products.Values.FirstOrDefault(p => p.Variants.Any(v => v.Sku == sku))?.Id);
    public Task<bool> AnyInCategoryAsync(Guid categoryId, CancellationToken ct) => Task.FromResult(Products.Values.Any(p => p.CategoryIds.Contains(categoryId)));
    public Task<bool> AnyWithBrandAsync(Guid brandId, CancellationToken ct) => Task.FromResult(Products.Values.Any(p => p.Brand?.Id == brandId));
    public Task InsertAsync(Product product, CancellationToken ct) { Products[product.Id] = product; return Task.CompletedTask; }

    public Task ReplaceAsync(Product product, long expectedVersion, CancellationToken ct)
    {
        // Simulates a concurrent save: the stored version moved on after our read.
        if (RejectNextReplace) { RejectNextReplace = false; throw new CatalogException(CatalogErrors.VersionMismatch, "stale", FailureKind.PreconditionFailed); }
        Products[product.Id] = product;
        return Task.CompletedTask;
    }
    public bool RejectNextReplace { get; set; }
    public Task<IReadOnlyList<string>> UpdateBrandRefAsync(BrandRef brand, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    // categories
    Task<Category?> ICategoryRepository.GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Categories.GetValueOrDefault(id));
    public Task<Category?> GetBySlugAsync(string slug, CancellationToken ct) => Task.FromResult(Categories.Values.FirstOrDefault(c => c.Slug == slug));
    public Task<IReadOnlyList<Category>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Category>>(ids.Where(Categories.ContainsKey).Select(i => Categories[i]).ToList());
    public Task<IReadOnlyList<Category>> ListAsync(bool activeOnly, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Category>>(Categories.Values.Where(c => !activeOnly || c.IsActive).ToList());
    public Task<IReadOnlyList<Guid>> GetSelfAndDescendantIdsAsync(string slug, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Categories.Values.Where(c => c.IsActive && c.Path.Contains(slug)).Select(c => c.Id).ToList());
    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct) => Task.FromResult(Categories.Values.Any(c => c.Slug == slug));
    Task<IReadOnlySet<string>> ICategoryRepository.SlugsStartingWithAsync(string prefix, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(Categories.Values.Select(c => c.Slug).Where(s => s.StartsWith(prefix)).ToHashSet());
    public Task<bool> HasChildrenAsync(Guid id, CancellationToken ct) => Task.FromResult(Categories.Values.Any(c => c.ParentId == id));
    public Task InsertAsync(Category category, CancellationToken ct) { Categories[category.Id] = category; return Task.CompletedTask; }
    public Task ReplaceAsync(Category category, long expectedVersion, CancellationToken ct) { Categories[category.Id] = category; return Task.CompletedTask; }
    Task ICategoryRepository.DeleteAsync(Guid id, CancellationToken ct) { Categories.Remove(id); return Task.CompletedTask; }

    // brands
    Task<Brand?> IBrandRepository.GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Brands.GetValueOrDefault(id));
    Task<IReadOnlyList<Brand>> IBrandRepository.ListAsync(bool activeOnly, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Brand>>(Brands.Values.Where(b => !activeOnly || b.IsActive).ToList());
    Task<IReadOnlySet<string>> IBrandRepository.SlugsStartingWithAsync(string prefix, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(Brands.Values.Select(b => b.Slug).Where(s => s.StartsWith(prefix)).ToHashSet());
    Task<bool> IBrandRepository.SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct) => Task.FromResult(Brands.Values.Any(b => b.Slug == slug && b.Id != exceptId));
    public Task InsertAsync(Brand brand, CancellationToken ct) { Brands[brand.Id] = brand; return Task.CompletedTask; }
    public Task ReplaceAsync(Brand brand, long expectedVersion, CancellationToken ct) { Brands[brand.Id] = brand; return Task.CompletedTask; }
    Task IBrandRepository.DeleteAsync(Guid id, CancellationToken ct) { Brands.Remove(id); return Task.CompletedTask; }
}

internal sealed class SpyCache : ICatalogCache
{
    public Dictionary<string, object> Entries { get; } = [];
    public List<string> Removed { get; } = [];

    public async Task<T?> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T?>> factory, CancellationToken ct) where T : class
    {
        if (Entries.TryGetValue(key, out var cached)) return (T)cached;
        var value = await factory(ct);
        if (value is not null) Entries[key] = value;
        return value;
    }

    public Task RemoveAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        foreach (var key in keys) { Removed.Add(key); Entries.Remove(key); }
        return Task.CompletedTask;
    }
}

public abstract class ApplicationTestBase
{
    internal FakeStore Store { get; } = new();
    internal SpyCache Cache { get; } = new();
    internal ISender Sender { get; }

    protected ApplicationTestBase()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProductRepository>(Store);
        services.AddSingleton<ICategoryRepository>(Store);
        services.AddSingleton<IBrandRepository>(Store);
        services.AddSingleton<ICatalogCache>(Cache);
        services.AddSingleton<IOptions<CatalogOptions>>(Options.Create(new CatalogOptions()));
        services.AddCatalogApplication();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero)));
        Sender = services.BuildServiceProvider().GetRequiredService<ISender>();
    }

    protected static ProductDetailsRequest Details(string name = "MacBook Air", string? slug = null, Guid? category = null, string? description = "Fast") =>
        new(name, slug, description, null, category is null ? null : [category.Value], category, null, null, null);

    protected static VariantRequest Variant(decimal price = 1000m) => new(new Dictionary<string, string>(), price, null, 0, null);

    protected async Task<AdminProductDto> CreateDraft(string name = "MacBook Air", Guid? category = null) =>
        await Sender.Send(new CreateProductCommand(Details(name, category: category)));

    protected async Task<AdminProductDto> CreatePublished(string name = "MacBook Air")
    {
        var cat = await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Laptops " + Guid.NewGuid().ToString("N")[..6], null, null, 0)));
        var p = await CreateDraft(name, cat.Id);
        p = await Sender.Send(new UpsertVariantCommand(p.Id, "SKU-" + p.Slug, p.Version, Variant()));
        p = await Sender.Send(new SetImagesCommand(p.Id, p.Version, new SetImagesRequest([new ImageRequest("https://img.example.com/a.jpg", null, null)])));
        return await Sender.Send(new ChangeProductStatusCommand(p.Id, p.Version, ProductAction.Publish));
    }
}

public class ProductHandlerTests : ApplicationTestBase
{
    [Fact]
    public async Task F03_BR01_CreatingTwoProductsWithTheSameName_SuffixesTheSecondSlug()
    {
        var first = await CreateDraft("MacBook Air");
        var second = await CreateDraft("MacBook Air");
        var third = await CreateDraft("MacBook Air");

        first.Slug.Should().Be("macbook-air");
        second.Slug.Should().Be("macbook-air-2");
        third.Slug.Should().Be("macbook-air-3");
    }

    [Fact]
    public async Task F03_BR01_ExplicitSlug_ThatIsTaken_IsAConflict()
    {
        await CreateDraft("MacBook Air");

        var act = () => Sender.Send(new CreateProductCommand(Details("Other", slug: "macbook-air")));

        (await act.Should().ThrowAsync<CatalogException>()).Which.Should().Match<CatalogException>(e =>
            e.Code == CatalogErrors.SlugTaken && e.Kind == FailureKind.Conflict);
    }

    [Fact]
    public async Task F03_BR01_AdminCanChangeTheSlug_AndItMustStayUnique()
    {
        var a = await CreateDraft("Alpha");
        var b = await CreateDraft("Beta");

        var renamed = await Sender.Send(new UpdateProductCommand(a.Id, a.Version, Details("Alpha", slug: "alpha-new")));
        renamed.Slug.Should().Be("alpha-new");

        var act = () => Sender.Send(new UpdateProductCommand(b.Id, b.Version, Details("Beta", slug: "alpha-new")));
        (await act.Should().ThrowAsync<CatalogException>()).Which.Code.Should().Be(CatalogErrors.SlugTaken);
    }

    [Fact]
    public async Task F03_AC2_VariantWithASkuOfAnotherProduct_IsA409()
    {
        var a = await CreateDraft("Alpha");
        var b = await CreateDraft("Beta");
        await Sender.Send(new UpsertVariantCommand(a.Id, "SHARED-1", a.Version, Variant()));

        var act = () => Sender.Send(new UpsertVariantCommand(b.Id, "shared-1", b.Version, Variant()));

        var e = (await act.Should().ThrowAsync<CatalogException>()).Which;
        e.Code.Should().Be(CatalogErrors.SkuTaken);
        e.Kind.Should().Be(FailureKind.Conflict);
    }

    [Fact]
    public async Task F03_AC3_SecondAdminWithAStaleVersion_GetsAMismatch()
    {
        var p = await CreateDraft();
        await Sender.Send(new UpdateProductCommand(p.Id, p.Version, Details("First edit")));

        var act = () => Sender.Send(new UpdateProductCommand(p.Id, p.Version, Details("Second edit")));

        var e = (await act.Should().ThrowAsync<CatalogException>()).Which;
        e.Code.Should().Be(CatalogErrors.VersionMismatch);
        e.Kind.Should().Be(FailureKind.PreconditionFailed);
        Store.Products[p.Id].Name.Should().Be("First edit");
    }

    [Fact]
    public async Task F03_AC3_ARaceDetectedByTheRepository_SurfacesAsAMismatchAndSkipsCacheInvalidation()
    {
        var p = await CreateDraft();
        Store.RejectNextReplace = true;
        Cache.Removed.Clear();

        var act = () => Sender.Send(new UpdateProductCommand(p.Id, p.Version, Details("Edit")));

        (await act.Should().ThrowAsync<CatalogException>()).Which.Code.Should().Be(CatalogErrors.VersionMismatch);
        Cache.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task F03_AC1_PublishingWithoutImages_Returns422WithDetails()
    {
        var cat = await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Laptops", null, null, 0)));
        var p = await CreateDraft(category: cat.Id);
        p = await Sender.Send(new UpsertVariantCommand(p.Id, "SKU-1", p.Version, Variant()));

        var act = () => Sender.Send(new ChangeProductStatusCommand(p.Id, p.Version, ProductAction.Publish));

        var e = (await act.Should().ThrowAsync<CatalogException>()).Which;
        e.Code.Should().Be(CatalogErrors.PublishRequirementsNotMet);
        e.Errors.Should().ContainKey("images");
    }

    [Fact]
    public async Task F03_AC4_ChangingAPrice_InvalidatesTheProductCacheEntry()
    {
        var p = await CreatePublished("Phone");
        await Sender.Send(new GetProductBySlugQuery(p.Slug)); // warm the cache
        Cache.Entries.Should().ContainKey(CacheKeys.Product(p.Slug));

        await Sender.Send(new UpsertVariantCommand(p.Id, p.Variants[0].Sku, p.Version, Variant(price: 1200m)));

        Cache.Removed.Should().Contain(CacheKeys.Product(p.Slug));
        Cache.Entries.Should().NotContainKey(CacheKeys.Product(p.Slug));
        (await Sender.Send(new GetProductBySlugQuery(p.Slug))).Variants.Single().Price.Should().Be(1200m);
    }

    [Fact]
    public async Task F03_AC4_ChangingTheSlug_InvalidatesBothTheOldAndTheNewKey()
    {
        var p = await CreatePublished("Phone");

        await Sender.Send(new UpdateProductCommand(p.Id, p.Version, Details("Phone", slug: "phone-renamed", category: p.PrimaryCategoryId, description: "Fast")));

        Cache.Removed.Should().Contain([CacheKeys.Product("phone"), CacheKeys.Product("phone-renamed")]);
    }

    [Fact]
    public async Task F03_AC6_ArchivedProduct_Is404PublicallyButStillReadableByAdmin()
    {
        var p = await CreatePublished("Phone");
        (await Sender.Send(new GetProductBySlugQuery("phone"))).Name.Should().Be("Phone");

        var archived = await Sender.Send(new ChangeProductStatusCommand(p.Id, p.Version, ProductAction.Archive));

        var act = () => Sender.Send(new GetProductBySlugQuery("phone"));
        (await act.Should().ThrowAsync<CatalogException>()).Which.Kind.Should().Be(FailureKind.NotFound);
        (await Sender.Send(new GetAdminProductQuery(p.Id))).Status.Should().Be(ProductStatus.Archived);
        (await Sender.Send(new ListProductsQuery(Ids: [p.Id]))).Items.Should().BeEmpty();
        archived.Status.Should().Be(ProductStatus.Archived);
    }

    [Fact]
    public async Task F03_DraftProduct_IsNotVisibleOnTheStorefront()
    {
        var p = await CreateDraft("Hidden");

        var act = () => Sender.Send(new GetProductBySlugQuery(p.Slug));

        (await act.Should().ThrowAsync<CatalogException>()).Which.Kind.Should().Be(FailureKind.NotFound);
    }

    [Fact]
    public async Task F03_Create_WithAnInactiveOrUnknownCategoryOrBrand_IsRejectedWithFieldErrors()
    {
        var unknown = Guid.NewGuid();

        var act = () => Sender.Send(new CreateProductCommand(Details(category: unknown) with { BrandId = Guid.NewGuid() }));

        var e = (await act.Should().ThrowAsync<CatalogException>()).Which;
        e.Errors.Should().ContainKey("brandId").And.ContainKey("primaryCategoryId");
    }

    [Fact]
    public async Task F03_US036_Create_FirstCategoryBecomesPrimaryWhenNoneIsGiven()
    {
        var cat = await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Laptops", null, null, 0)));

        var p = await Sender.Send(new CreateProductCommand(Details() with { CategoryIds = [cat.Id], PrimaryCategoryId = null }));

        p.PrimaryCategoryId.Should().Be(cat.Id);
        p.CategoryPath.Should().Equal("laptops");
    }

    [Fact]
    public async Task F03_Listing_BatchIdsAreLimitedToFifty()
    {
        var act = () => Sender.Send(new ListProductsQuery(Ids: Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToList()));

        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
    }
}

public class CategoryAndBrandHandlerTests : ApplicationTestBase
{
    [Fact]
    public async Task F03_US031_Tree_ContainsOnlyActiveCategoriesOrderedBySort()
    {
        var root = await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Electronics", null, null, 2)));
        await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Books", null, null, 1)));
        var hidden = await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Computers", null, root.Id, 0)));
        await Sender.Send(new UpdateCategoryCommand(hidden.Id, hidden.Version, new CategoryRequest("Computers", null, null, 0, IsActive: false)));

        var tree = await Sender.Send(new GetCategoryTreeQuery());

        tree.Select(n => n.Slug).Should().Equal("books", "electronics");
        tree.Single(n => n.Slug == "electronics").Children.Should().BeEmpty();
    }

    [Fact]
    public async Task F03_US034_Update_WithAStaleVersion_IsAMismatch()
    {
        var c = await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Books", null, null, 0)));
        await Sender.Send(new UpdateCategoryCommand(c.Id, c.Version, new CategoryRequest("Books 2", null, null, 0)));

        var act = () => Sender.Send(new UpdateCategoryCommand(c.Id, c.Version, new CategoryRequest("Books 3", null, null, 0)));

        (await act.Should().ThrowAsync<CatalogException>()).Which.Code.Should().Be(CatalogErrors.VersionMismatch);
    }

    [Fact]
    public async Task F03_BR09_CategoryInUse_CannotBeDeleted()
    {
        var published = await CreatePublished("Phone");

        var act = () => Sender.Send(new DeleteCategoryCommand(published.PrimaryCategoryId!.Value));

        (await act.Should().ThrowAsync<CatalogException>()).Which.Code.Should().Be(CatalogErrors.CategoryHasProducts);
    }

    [Fact]
    public async Task F03_BR09_CategoryWithChildren_CannotBeDeleted_ButAnUnusedLeafCan()
    {
        var parent = await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Parent", null, null, 0)));
        var child = await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Child", null, parent.Id, 0)));

        var blocked = () => Sender.Send(new DeleteCategoryCommand(parent.Id));
        (await blocked.Should().ThrowAsync<CatalogException>()).Which.Code.Should().Be(CatalogErrors.CategoryHasChildren);

        await Sender.Send(new DeleteCategoryCommand(child.Id));
        await Sender.Send(new DeleteCategoryCommand(parent.Id));
        Store.Categories.Should().BeEmpty();
        Cache.Removed.Should().Contain(CacheKeys.CategoryTree);
    }

    [Fact]
    public async Task F03_US035_Brand_SlugIsGeneratedAndCollisionsGetASuffix()
    {
        var a = await Sender.Send(new CreateBrandCommand(new BrandRequest("Apple", null, null)));
        var b = await Sender.Send(new CreateBrandCommand(new BrandRequest("Apple", null, null)));

        a.Slug.Should().Be("apple");
        b.Slug.Should().Be("apple-2");
        (await Sender.Send(new ListBrandsQuery())).Should().HaveCount(2);
    }

    [Fact]
    public async Task F03_US035_Brand_InUseByAProduct_CannotBeDeleted()
    {
        var brand = await Sender.Send(new CreateBrandCommand(new BrandRequest("Apple", null, null)));
        await Sender.Send(new CreateProductCommand(Details() with { BrandId = brand.Id }));

        var act = () => Sender.Send(new DeleteBrandCommand(brand.Id));

        (await act.Should().ThrowAsync<CatalogException>()).Which.Code.Should().Be(CatalogErrors.BrandInUse);
    }

    [Fact]
    public async Task F03_US031_CategoryBySlug_ReturnsTheAttributeSchema_AndHidesInactiveOnes()
    {
        var c = await Sender.Send(new CreateCategoryCommand(new CategoryRequest("Laptops", null, null, 0, true,
            [new AttributeDefinitionDto("cpu", "CPU", "text", null, null, true, true)])));

        (await Sender.Send(new GetCategoryBySlugQuery("laptops"))).AttributeSchema.Should().ContainSingle(a => a.Key == "cpu" && a.Filterable);

        await Sender.Send(new UpdateCategoryCommand(c.Id, c.Version, new CategoryRequest("Laptops", null, null, 0, false)));
        var act = () => Sender.Send(new GetCategoryBySlugQuery("laptops"));
        (await act.Should().ThrowAsync<CatalogException>()).Which.Kind.Should().Be(FailureKind.NotFound);
    }
}
