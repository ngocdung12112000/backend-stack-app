using System.Text.RegularExpressions;
using Catalog.Application.Abstractions;
using Catalog.Domain;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Catalog.Infrastructure.Persistence;

internal static class MongoErrors
{
    /// <summary>Runs a write and turns a unique-index violation into the matching business error.</summary>
    public static async Task WriteAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var message = ex.WriteError.Message;
            if (message.Contains(CatalogDb.SkuIndex, StringComparison.Ordinal))
                throw CatalogException.Conflict(CatalogErrors.SkuTaken, "A SKU of this product is already used by another product.");
            throw CatalogException.Conflict(CatalogErrors.SlugTaken, "The slug is already in use.");
        }
    }

    public static CatalogException VersionMismatch() => new(CatalogErrors.VersionMismatch,
        "The record was changed by someone else. Reload it and try again.", FailureKind.PreconditionFailed);
}

internal sealed class ProductRepository(CatalogDb db) : IProductRepository
{
    private IMongoCollection<Product> Col => db.Products;

    public async Task<Product?> GetByIdAsync(Guid id, CancellationToken ct) =>
        await Col.Find(p => p.Id == id).FirstOrDefaultAsync(ct);

    public async Task<Product?> GetPublishedBySlugAsync(string slug, CancellationToken ct) =>
        await Col.Find(p => p.Slug == slug && p.Status == ProductStatus.Published).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Product>> GetPublishedByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var found = await Col.Find(p => ids.Contains(p.Id) && p.Status == ProductStatus.Published).ToListAsync(ct);
        var byId = found.ToDictionary(p => p.Id);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    public async Task<(IReadOnlyList<Product> Items, long Total)> QueryAsync(ProductQuery q, CancellationToken ct)
    {
        var f = Builders<Product>.Filter;
        var filters = new List<FilterDefinition<Product>>();
        if (q.Status is { } status) filters.Add(f.Eq(p => p.Status, status));
        if (q.CategoryIds is not null) filters.Add(f.AnyIn(p => p.CategoryIds, q.CategoryIds));
        if (q.BrandSlug is not null) filters.Add(f.Eq("brand.slug", q.BrandSlug));
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var rx = new BsonRegularExpression(Regex.Escape(q.Text), "i");
            filters.Add(f.Or(f.Regex(p => p.Name, rx), f.Regex(p => p.Slug, rx), f.Regex("variants.sku", rx)));
        }

        var filter = filters.Count == 0 ? FilterDefinition<Product>.Empty : f.And(filters);
        var s = Builders<Product>.Sort;
        var sort = q.Sort switch
        {
            ProductSort.PriceAsc => s.Ascending(p => p.MinPrice).Descending(p => p.Id),
            ProductSort.PriceDesc => s.Descending(p => p.MinPrice).Descending(p => p.Id),
            ProductSort.Name => s.Ascending(p => p.Name).Ascending(p => p.Id),
            _ => q.Status == ProductStatus.Published ? s.Descending(p => p.PublishedAt).Descending(p => p.Id) : s.Descending(p => p.UpdatedAt).Descending(p => p.Id),
        };

        // Cards and list rows never show the long fields.
        var projection = Builders<Product>.Projection.Exclude(p => p.Description).Exclude(p => p.Attributes);
        var total = await Col.CountDocumentsAsync(filter, cancellationToken: ct);
        var find = Col.Find(filter, q.Sort == ProductSort.Name ? new FindOptions { Collation = new Collation("en", strength: CollationStrength.Secondary) } : null)
            .Sort(sort).Skip((q.Page - 1) * q.PageSize).Limit(q.PageSize).Project<Product>(projection);
        return (await find.ToListAsync(ct), total);
    }

    public async Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct) =>
        await Col.Find(p => p.Slug == slug && (exceptId == null || p.Id != exceptId)).Limit(1).AnyAsync(ct);

    public async Task<IReadOnlySet<string>> SlugsStartingWithAsync(string prefix, CancellationToken ct)
    {
        var slugs = await Col.Find(Builders<Product>.Filter.Regex(p => p.Slug, new BsonRegularExpression("^" + Regex.Escape(prefix))))
            .Project(p => p.Slug).ToListAsync(ct);
        return slugs.ToHashSet();
    }

    public async Task<Guid?> FindProductIdBySkuAsync(string sku, CancellationToken ct)
    {
        var ids = await Col.Find(Builders<Product>.Filter.Eq("variants.sku", sku)).Limit(1).Project(p => p.Id).ToListAsync(ct);
        return ids.Count == 0 ? null : ids[0];
    }

    public async Task<bool> AnyInCategoryAsync(Guid categoryId, CancellationToken ct) =>
        await Col.Find(Builders<Product>.Filter.AnyEq(p => p.CategoryIds, categoryId)).Limit(1).AnyAsync(ct);

    public async Task<bool> AnyWithBrandAsync(Guid brandId, CancellationToken ct) =>
        await Col.Find(Builders<Product>.Filter.Eq("brand.id", brandId)).Limit(1).AnyAsync(ct);

    public Task InsertAsync(Product product, CancellationToken ct) =>
        MongoErrors.WriteAsync(() => Col.InsertOneAsync(product, cancellationToken: ct));

    public Task ReplaceAsync(Product product, long expectedVersion, CancellationToken ct) =>
        MongoErrors.WriteAsync(async () =>
        {
            var result = await Col.ReplaceOneAsync(p => p.Id == product.Id && p.Version == expectedVersion, product, cancellationToken: ct);
            if (result.MatchedCount == 0) throw MongoErrors.VersionMismatch(); // BR-08: someone saved between our read and write
        });

    public async Task<IReadOnlyList<string>> UpdateBrandRefAsync(BrandRef brand, CancellationToken ct)
    {
        var filter = Builders<Product>.Filter.Eq("brand.id", brand.Id);
        var slugs = await Col.Find(filter).Project(p => p.Slug).ToListAsync(ct);
        if (slugs.Count > 0) await Col.UpdateManyAsync(filter, Builders<Product>.Update.Set(p => p.Brand, brand), cancellationToken: ct);
        return slugs;
    }
}

internal sealed class CategoryRepository(CatalogDb db) : ICategoryRepository
{
    private IMongoCollection<Category> Col => db.Categories;

    public async Task<Category?> GetByIdAsync(Guid id, CancellationToken ct) => await Col.Find(c => c.Id == id).FirstOrDefaultAsync(ct);
    public async Task<Category?> GetBySlugAsync(string slug, CancellationToken ct) => await Col.Find(c => c.Slug == slug).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Category>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        return await Col.Find(c => list.Contains(c.Id)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Category>> ListAsync(bool activeOnly, CancellationToken ct) =>
        await (activeOnly ? Col.Find(c => c.IsActive) : Col.Find(FilterDefinition<Category>.Empty)).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetSelfAndDescendantIdsAsync(string slug, CancellationToken ct)
    {
        var root = await GetBySlugAsync(slug, ct);
        if (root is not { IsActive: true }) return [];
        return await Col.Find(Builders<Category>.Filter.AnyEq(c => c.Path, slug) & Builders<Category>.Filter.Eq(c => c.IsActive, true))
            .Project(c => c.Id).ToListAsync(ct);
    }

    public async Task<bool> SlugExistsAsync(string slug, CancellationToken ct) => await Col.Find(c => c.Slug == slug).Limit(1).AnyAsync(ct);

    public async Task<IReadOnlySet<string>> SlugsStartingWithAsync(string prefix, CancellationToken ct) =>
        (await Col.Find(Builders<Category>.Filter.Regex(c => c.Slug, new BsonRegularExpression("^" + Regex.Escape(prefix)))).Project(c => c.Slug).ToListAsync(ct)).ToHashSet();

    public async Task<bool> HasChildrenAsync(Guid id, CancellationToken ct) => await Col.Find(c => c.ParentId == id).Limit(1).AnyAsync(ct);

    public Task InsertAsync(Category category, CancellationToken ct) => MongoErrors.WriteAsync(() => Col.InsertOneAsync(category, cancellationToken: ct));

    public Task ReplaceAsync(Category category, long expectedVersion, CancellationToken ct) =>
        MongoErrors.WriteAsync(async () =>
        {
            var result = await Col.ReplaceOneAsync(c => c.Id == category.Id && c.Version == expectedVersion, category, cancellationToken: ct);
            if (result.MatchedCount == 0) throw MongoErrors.VersionMismatch();
        });

    public async Task DeleteAsync(Guid id, CancellationToken ct) => await Col.DeleteOneAsync(c => c.Id == id, ct);
}

internal sealed class BrandRepository(CatalogDb db) : IBrandRepository
{
    private IMongoCollection<Brand> Col => db.Brands;

    public async Task<Brand?> GetByIdAsync(Guid id, CancellationToken ct) => await Col.Find(b => b.Id == id).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Brand>> ListAsync(bool activeOnly, CancellationToken ct) =>
        await (activeOnly ? Col.Find(b => b.IsActive) : Col.Find(FilterDefinition<Brand>.Empty)).SortBy(b => b.Name).ToListAsync(ct);

    public async Task<IReadOnlySet<string>> SlugsStartingWithAsync(string prefix, CancellationToken ct) =>
        (await Col.Find(Builders<Brand>.Filter.Regex(b => b.Slug, new BsonRegularExpression("^" + Regex.Escape(prefix)))).Project(b => b.Slug).ToListAsync(ct)).ToHashSet();

    public async Task<bool> SlugExistsAsync(string slug, Guid? exceptId, CancellationToken ct) =>
        await Col.Find(b => b.Slug == slug && (exceptId == null || b.Id != exceptId)).Limit(1).AnyAsync(ct);

    public Task InsertAsync(Brand brand, CancellationToken ct) => MongoErrors.WriteAsync(() => Col.InsertOneAsync(brand, cancellationToken: ct));

    public Task ReplaceAsync(Brand brand, long expectedVersion, CancellationToken ct) =>
        MongoErrors.WriteAsync(async () =>
        {
            var result = await Col.ReplaceOneAsync(b => b.Id == brand.Id && b.Version == expectedVersion, brand, cancellationToken: ct);
            if (result.MatchedCount == 0) throw MongoErrors.VersionMismatch();
        });

    public async Task DeleteAsync(Guid id, CancellationToken ct) => await Col.DeleteOneAsync(b => b.Id == id, ct);
}
