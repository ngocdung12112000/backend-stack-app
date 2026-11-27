using System.Net;
using System.Text.Json;
using Catalog.Application.Abstractions;
using Catalog.Domain;
using Catalog.Infrastructure.Caching;
using Catalog.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Catalog.IntegrationTests;

[Collection(CatalogCollection.Name)]
public class AdminProductTests(CatalogFactory factory)
{
    private readonly CatalogClient _admin = factory.Admin();
    private readonly CatalogClient _anon = factory.Anonymous();

    // A dictionary, not an anonymous object: the JSON serializer would camelCase property names, dictionary keys are kept as given.
    private static Dictionary<string, string> Opt(string name, string value) => new() { [name] = value };

    [Fact]
    public async Task F03_AC1_PublishWithoutImages_Returns422WithDetails()
    {
        var category = (await _admin.CreateCategory()).GetProperty("id").GetGuid();
        var p = await _admin.CreateDraft(category);
        p = await _admin.UpsertVariant(p, "AC1-" + Guid.NewGuid().ToString("N")[..8], 50m);

        var (response, body) = await _admin.Mutate(p, HttpMethod.Post, "/publish");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.GetProperty("code").GetString().Should().Be("CATALOG_PUBLISH_REQUIREMENTS_NOT_MET");
        body.GetProperty("errors").GetProperty("images")[0].GetString().Should().Contain("image");
        body.GetProperty("errors").TryGetProperty("variants", out _).Should().BeFalse();
    }

    [Fact]
    public async Task F03_US036_DraftToPublishedToArchived_FullLifecycle()
    {
        var published = await _admin.CreatePublished("Lifecycle Phone");

        published.GetProperty("status").GetString().Should().Be("Published");
        var slug = published.GetProperty("slug").GetString()!;
        (await _anon.Get($"/api/v1/products/{slug}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var unpublished = await _admin.MutateOk(published, HttpMethod.Post, "/unpublish");
        (await _anon.Get($"/api/v1/products/{slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var republished = await _admin.MutateOk(unpublished, HttpMethod.Post, "/publish");
        (await _anon.Get($"/api/v1/products/{slug}")).StatusCode.Should().Be(HttpStatusCode.OK);

        await _admin.MutateOk(republished, HttpMethod.Post, "/archive");
        (await _anon.Get($"/api/v1/products/{slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task F03_AC2_VariantWithADuplicateSku_EvenInAnotherProduct_Returns409()
    {
        var sku = "DUP-" + Guid.NewGuid().ToString("N")[..8];
        var a = await _admin.CreateDraft();
        var b = await _admin.CreateDraft();
        await _admin.UpsertVariant(a, sku, 10m);

        var (response, body) = await _admin.Mutate(b, HttpMethod.Put, $"/variants/{sku.ToLowerInvariant()}",
            new { options = new { }, price = 10m, weightG = 1, isActive = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        body.GetProperty("code").GetString().Should().Be("CATALOG_SKU_TAKEN");
    }

    [Fact]
    public async Task F03_BR02_UniqueSkuIndex_RejectsConcurrentCreationsAtTheDatabase()
    {
        var sku = "RACE-" + Guid.NewGuid().ToString("N")[..8];
        var products = new List<JsonElement>();
        for (var i = 0; i < 8; i++) products.Add(await _admin.CreateDraft());

        var results = await Task.WhenAll(products.Select(p =>
            _admin.Mutate(p, HttpMethod.Put, $"/variants/{sku}", new { options = new { }, price = 5m, weightG = 1, isActive = true })));

        results.Count(r => r.Response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(r => r.Response.StatusCode == HttpStatusCode.Conflict).Should().Be(7);
        (await factory.Db.Products.CountDocumentsAsync(Builders<Product>.Filter.Eq("variants.sku", sku.ToUpperInvariant()))).Should().Be(1);
    }

    [Fact]
    public async Task F03_BR02_ProductsWithoutVariants_DoNotCollideOnTheUniqueSkuIndex()
    {
        // An empty array indexes as a single null key; the partial index must keep drafts from clashing.
        var a = await _admin.CreateDraft();
        var b = await _admin.CreateDraft();

        a.GetProperty("id").GetGuid().Should().NotBe(b.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task F03_AC3_SecondAdminWithAStaleIfMatch_Gets412_AndCanRetryAfterReloading()
    {
        var draft = await _admin.CreateDraft(name: "Contested");
        var id = draft.GetProperty("id").GetGuid();
        var staleEtag = $"\"{draft.GetProperty("version").GetInt64()}\"";
        object Body(string name) => new { name, slug = draft.GetProperty("slug").GetString(), description = "d" };

        var first = await _admin.Send(HttpMethod.Put, $"/api/v1/admin/products/{id}", Body("Admin A"), staleEtag);
        var second = await _admin.Send(HttpMethod.Put, $"/api/v1/admin/products/{id}", Body("Admin B"), staleEtag);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        CatalogClient.ETag(first).Should().Be("\"2\"");
        second.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await CatalogClient.Code(second)).Should().Be("CATALOG_VERSION_MISMATCH");

        var reloaded = await _admin.Get($"/api/v1/admin/products/{id}");
        var retry = await _admin.Send(HttpMethod.Put, $"/api/v1/admin/products/{id}", Body("Admin B"), CatalogClient.ETag(reloaded));
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F03_AC3_ParallelUpdatesWithTheSameVersion_ExactlyOneWins()
    {
        var draft = await _admin.CreateDraft();
        var id = draft.GetProperty("id").GetGuid();
        var etag = $"\"{draft.GetProperty("version").GetInt64()}\"";

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            _admin.Send(HttpMethod.Put, $"/api/v1/admin/products/{id}",
                new { name = $"Racer {i}", slug = draft.GetProperty("slug").GetString(), description = "d" }, etag)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed).Should().Be(9);
    }

    [Fact]
    public async Task F03_BR08_MissingIfMatch_Returns428_AndGarbageIfMatchReturns400()
    {
        var p = await _admin.CreateDraft();
        var id = p.GetProperty("id").GetGuid();
        var body = new { name = "x", slug = p.GetProperty("slug").GetString() };

        var missing = await _admin.Send(HttpMethod.Put, $"/api/v1/admin/products/{id}", body);
        var garbage = await _admin.Send(HttpMethod.Put, $"/api/v1/admin/products/{id}", body, "\"abc\"");

        missing.StatusCode.Should().Be((HttpStatusCode)428);
        (await CatalogClient.Code(missing)).Should().Be("CATALOG_IF_MATCH_REQUIRED");
        garbage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task F03_AC5_AttributesNotMatchingTheCategorySchema_AreRejectedWithFieldErrors()
    {
        var schema = new object[]
        {
            new { key = "cpu", label = "CPU", type = "text", filterable = true, required = true },
            new { key = "ramGb", label = "RAM", type = "number", unit = "GB", filterable = true, required = false },
            new { key = "color", label = "Color", type = "enum", options = new[] { "Silver", "Black" }, filterable = false, required = false },
        };
        var category = (await _admin.CreateCategory(schema: schema)).GetProperty("id").GetGuid();

        var bad = await _admin.Send(HttpMethod.Post, "/api/v1/admin/products", new
        {
            name = CatalogClient.Unique(),
            categoryIds = new[] { category },
            primaryCategoryId = category,
            attributes = new { ramGb = "lots", color = "Pink", unknown = 1 },
        });

        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await CatalogClient.Json(bad);
        body.GetProperty("code").GetString().Should().Be("CATALOG_INVALID_ATTRIBUTES");
        var errors = body.GetProperty("errors");
        errors.TryGetProperty("attributes.cpu", out _).Should().BeTrue("required attribute is missing");
        errors.TryGetProperty("attributes.ramGb", out _).Should().BeTrue();
        errors.TryGetProperty("attributes.color", out _).Should().BeTrue();
        errors.TryGetProperty("attributes.unknown", out _).Should().BeTrue();

        var good = await _admin.CreateDraft(category, attributes: new { cpu = "M3", ramGb = 16, color = "Silver" });
        good.GetProperty("attributes").GetProperty("ramGb").GetInt32().Should().Be(16);
    }

    [Fact]
    public async Task F03_AC6_ArchivedProduct_Is404Publicly_ButAdminStillReadsIt_AndBatchSkipsIt()
    {
        var published = await _admin.CreatePublished("Soon Archived");
        var id = published.GetProperty("id").GetGuid();
        var slug = published.GetProperty("slug").GetString()!;
        (await _anon.Get($"/api/v1/products/{slug}")).StatusCode.Should().Be(HttpStatusCode.OK);

        await _admin.MutateOk(published, HttpMethod.Post, "/archive");

        (await _anon.Get($"/api/v1/products/{slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var admin = await _admin.Get($"/api/v1/admin/products/{id}");
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CatalogClient.Json(admin)).GetProperty("status").GetString().Should().Be("Archived");
        var batch = await CatalogClient.Json(await _anon.Get($"/api/v1/products?ids={id}"));
        batch.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task F03_BR01_TwoProductsWithTheSameName_GetDashTwoSlug()
    {
        var name = CatalogClient.Unique("Same Name");

        var first = await _admin.CreateDraft(name: name);
        var second = await _admin.CreateDraft(name: name);

        second.GetProperty("slug").GetString().Should().Be(first.GetProperty("slug").GetString() + "-2");
    }

    [Fact]
    public async Task F03_BR01_ExplicitSlugThatIsTaken_Returns409()
    {
        var first = await _admin.CreateDraft();

        var response = await _admin.Send(HttpMethod.Post, "/api/v1/admin/products",
            new { name = "Other", slug = first.GetProperty("slug").GetString() });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CatalogClient.Code(response)).Should().Be("CATALOG_SLUG_TAKEN");
    }

    [Fact]
    public async Task F03_BR03_BR06_VariantRules_AreEnforcedOverHttp()
    {
        var p = await _admin.CreateDraft();
        p = await _admin.MutateOk(p, HttpMethod.Put, "/options", new { options = new[] { new { name = "Color", values = new[] { "Red", "Blue" } } } });

        var prefix = "VR-" + Guid.NewGuid().ToString("N")[..6];
        p = await _admin.UpsertVariant(p, prefix + "-R", 10m, Opt("Color", "Red"), compareAt: 15m);

        var wrongValue = await _admin.Mutate(p, HttpMethod.Put, $"/variants/{prefix}-X", new { options = Opt("Color", "Green"), price = 10m });
        var sameCombo = await _admin.Mutate(p, HttpMethod.Put, $"/variants/{prefix}-R2", new { options = Opt("Color", "Red"), price = 10m });
        var badCompare = await _admin.Mutate(p, HttpMethod.Put, $"/variants/{prefix}-B", new { options = Opt("Color", "Blue"), price = 10m, compareAtPrice = 10m });

        wrongValue.Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wrongValue.Body.GetProperty("code").GetString().Should().Be("CATALOG_INVALID_VARIANT");
        sameCombo.Response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        sameCombo.Body.GetProperty("code").GetString().Should().Be("CATALOG_VARIANT_COMBINATION_TAKEN");
        badCompare.Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        badCompare.Body.GetProperty("errors").TryGetProperty("compareAtPrice", out _).Should().BeTrue();
    }

    [Fact]
    public async Task F03_RemoveVariant_ReturnsTheUpdatedProduct_AndUnknownSkuIs404()
    {
        var p = await _admin.CreateDraft();
        var sku = "RM-" + Guid.NewGuid().ToString("N")[..6];
        p = await _admin.UpsertVariant(p, sku, 10m);

        var removed = await _admin.MutateOk(p, HttpMethod.Delete, $"/variants/{sku}");
        var again = await _admin.Mutate(removed, HttpMethod.Delete, $"/variants/{sku}");

        removed.GetProperty("variants").GetArrayLength().Should().Be(0);
        again.Response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        again.Body.GetProperty("code").GetString().Should().Be("CATALOG_VARIANT_NOT_FOUND");
    }

    [Fact]
    public async Task F03_AdminList_FiltersByStatusAndSearchesNameSlugAndSku()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var draft = await _admin.CreateDraft(name: $"Searchable {token} draft");
        var published = await _admin.CreatePublished($"Searchable {token} live", sku: $"FINDME-{token}");

        var all = await CatalogClient.Json(await _admin.Get($"/api/v1/admin/products?q={token}"));
        var onlyDrafts = await CatalogClient.Json(await _admin.Get($"/api/v1/admin/products?q={token}&status=Draft"));
        var bySku = await CatalogClient.Json(await _admin.Get($"/api/v1/admin/products?q=findme-{token}"));

        all.GetProperty("totalCount").GetInt64().Should().Be(2);
        onlyDrafts.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(draft.GetProperty("id").GetGuid());
        bySku.GetProperty("items").GetArrayLength().Should().Be(1);
        bySku.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(published.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task F03_Persistence_StoresReadableStringIdsEnumsAndSchemaVersion_AndRoundTripsEverything()
    {
        var brand = (await _admin.CreateBrand()).GetProperty("id").GetGuid();
        var category = (await _admin.CreateCategory(schema: new object[] { new { key = "weightKg", label = "Weight", type = "number", filterable = false, required = false } }))
            .GetProperty("id").GetGuid();
        var p = await _admin.CreateDraft(category, brand, attributes: new { weightKg = 1.24 });
        p = await _admin.MutateOk(p, HttpMethod.Put, "/options", new { options = new[] { new { name = "Size", values = new[] { "S", "M" } } } });
        p = await _admin.UpsertVariant(p, "RT-" + Guid.NewGuid().ToString("N")[..6], 19.99m, Opt("Size", "M"), 24.99m);
        p = await _admin.SetImages(p, "https://img.example.com/1.jpg", "https://img.example.com/2.jpg");
        p = await _admin.MutateOk(p, HttpMethod.Post, "/publish");

        var id = p.GetProperty("id").GetGuid().ToString();
        var raw = await factory.Db.Database.GetCollection<BsonDocument>("products").Find(new BsonDocument("_id", id)).SingleAsync();

        raw["status"].AsString.Should().Be("Published");
        raw["schemaVersion"].AsInt32.Should().Be(1);
        raw["version"].AsInt64.Should().Be(p.GetProperty("version").GetInt64());
        raw["variants"][0]["price"].BsonType.Should().Be(BsonType.Decimal128);
        raw["minPrice"].AsDecimal128.Should().Be(new Decimal128(19.99m));
        raw["attributes"]["weightKg"].AsDouble.Should().Be(1.24);
        raw["brand"]["slug"].AsString.Should().NotBeNullOrEmpty();
        raw["images"].AsBsonArray.Select(i => i["sort"].AsInt32).Should().Equal(0, 1);

        var back = await CatalogClient.Json(await _admin.Get($"/api/v1/admin/products/{id}"));
        back.GetProperty("variants")[0].GetProperty("price").GetDecimal().Should().Be(19.99m);
        back.GetProperty("variants")[0].GetProperty("options").GetProperty("Size").GetString().Should().Be("M");
        back.GetProperty("attributes").GetProperty("weightKg").GetDouble().Should().Be(1.24);
    }
}

[Collection(CatalogCollection.Name)]
public class StorefrontTests(CatalogFactory factory)
{
    private readonly CatalogClient _admin = factory.Admin();
    private readonly CatalogClient _anon = factory.Anonymous();

    [Fact]
    public async Task F03_US032_ProductDetail_ReturnsVariantsImagesBrandAndCategoryPath()
    {
        var brand = await _admin.CreateBrand("Acme " + Guid.NewGuid().ToString("N")[..5]);
        var root = await _admin.CreateCategory("Electronics " + Guid.NewGuid().ToString("N")[..5]);
        var leaf = await _admin.CreateCategory("Phones " + Guid.NewGuid().ToString("N")[..5], root.GetProperty("id").GetGuid());
        var p = await _admin.CreatePublished("Detail Phone", 499m, brand.GetProperty("id").GetGuid(), leaf.GetProperty("id").GetGuid());

        var response = await _anon.Get($"/api/v1/products/{p.GetProperty("slug").GetString()}");
        var body = await CatalogClient.Json(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("brand").GetProperty("name").GetString().Should().Be(brand.GetProperty("name").GetString());
        body.GetProperty("categoryPath").EnumerateArray().Select(e => e.GetString()).Should()
            .Equal(root.GetProperty("slug").GetString(), leaf.GetProperty("slug").GetString());
        body.GetProperty("variants")[0].GetProperty("price").GetDecimal().Should().Be(499m);
        body.GetProperty("images").GetArrayLength().Should().Be(1);
        body.TryGetProperty("version", out _).Should().BeFalse("internal fields are not public");
        body.TryGetProperty("status", out _).Should().BeFalse();
    }

    [Fact]
    public async Task F03_US032_UnknownAndDraftSlugs_Return404ProblemDetails()
    {
        var draft = await _admin.CreateDraft();

        var unknown = await _anon.Get("/api/v1/products/does-not-exist");
        var hidden = await _anon.Get($"/api/v1/products/{draft.GetProperty("slug").GetString()}");

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CatalogClient.Code(unknown)).Should().Be("CATALOG_PRODUCT_NOT_FOUND");
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task F03_Listing_KeepsTheLegacyShape_AndFiltersByCategoryBrandSortAndPage()
    {
        var brandJson = await _admin.CreateBrand();
        var brand = brandJson.GetProperty("id").GetGuid();
        var parent = await _admin.CreateCategory();
        var child = await _admin.CreateCategory(parentId: parent.GetProperty("id").GetGuid());
        var other = await _admin.CreateCategory();
        var cheap = await _admin.CreatePublished("Cheap", 10m, brand, child.GetProperty("id").GetGuid());
        var dear = await _admin.CreatePublished("Dear", 90m, brand, child.GetProperty("id").GetGuid());
        await _admin.CreatePublished("Elsewhere", 50m, categoryId: other.GetProperty("id").GetGuid());
        await _admin.CreateDraft(child.GetProperty("id").GetGuid());

        var byParent = await CatalogClient.Json(await _anon.Get($"/api/v1/products?category={parent.GetProperty("slug").GetString()}&sort=price_desc"));

        // The Phase 0 contract (api-client 0.1.x): items, page, pageSize, totalCount; each item has id, name, price, currency.
        byParent.GetProperty("page").GetInt32().Should().Be(1);
        byParent.GetProperty("pageSize").GetInt32().Should().Be(20);
        byParent.GetProperty("totalCount").GetInt64().Should().Be(2, "the parent category includes its sub-categories, drafts and other categories are excluded");
        var items = byParent.GetProperty("items");
        items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should()
            .Equal(dear.GetProperty("id").GetGuid(), cheap.GetProperty("id").GetGuid());
        items[0].GetProperty("price").GetDecimal().Should().Be(90m);
        items[0].GetProperty("currency").GetString().Should().Be("USD");
        items[0].GetProperty("name").GetString().Should().Be("Dear");
        items[0].GetProperty("imageUrl").GetString().Should().Be("https://img.example.com/1.jpg");

        var asc = await CatalogClient.Json(await _anon.Get($"/api/v1/products?category={child.GetProperty("slug").GetString()}&sort=price_asc&pageSize=1&page=2"));
        asc.GetProperty("totalCount").GetInt64().Should().Be(2);
        asc.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Dear");

        var byBrand = await CatalogClient.Json(await _anon.Get($"/api/v1/products?brand={brandJson.GetProperty("slug").GetString()}"));
        byBrand.GetProperty("totalCount").GetInt64().Should().Be(2);

        var unknownCategory = await CatalogClient.Json(await _anon.Get("/api/v1/products?category=nope-nope"));
        unknownCategory.GetProperty("totalCount").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task F03_Listing_BatchByIds_KeepsInputOrderAndRejectsBadIds()
    {
        var a = await _admin.CreatePublished("Batch A");
        var b = await _admin.CreatePublished("Batch B");
        var ids = $"{b.GetProperty("id").GetGuid()},{a.GetProperty("id").GetGuid()},{Guid.NewGuid()}";

        var ok = await CatalogClient.Json(await _anon.Get($"/api/v1/products?ids={ids}"));
        var bad = await _anon.Get("/api/v1/products?ids=not-a-guid");
        var tooMany = await _anon.Get("/api/v1/products?ids=" + string.Join(',', Enumerable.Range(0, 51).Select(_ => Guid.NewGuid())));

        ok.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).Should().Equal("Batch B", "Batch A");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task F03_US031_CategoryTree_IsNestedOrderedAndCached_AndInvalidatedOnWrite()
    {
        var name = "Tree " + Guid.NewGuid().ToString("N")[..6];
        var root = await _admin.CreateCategory(name);
        await _admin.CreateCategory("Child " + name, root.GetProperty("id").GetGuid());

        var tree = await CatalogClient.Json(await _anon.Get("/api/v1/categories/tree"));
        var node = tree.EnumerateArray().Single(n => n.GetProperty("id").GetGuid() == root.GetProperty("id").GetGuid());
        node.GetProperty("children").GetArrayLength().Should().Be(1);
        (await factory.Redis.KeyExistsAsync(CacheKeys.CategoryTree)).Should().BeTrue();

        await _admin.CreateCategory("Another " + name);

        (await factory.Redis.KeyExistsAsync(CacheKeys.CategoryTree)).Should().BeFalse("a category write invalidates the cached tree");
    }

    [Fact]
    public async Task F03_US031_CategoryBySlug_ReturnsTheAttributeSchemaForFacets()
    {
        var c = await _admin.CreateCategory(schema: new object[] { new { key = "cpu", label = "CPU", type = "text", filterable = true, required = false } });

        var body = await CatalogClient.Json(await _anon.Get($"/api/v1/categories/{c.GetProperty("slug").GetString()}"));

        body.GetProperty("attributeSchema")[0].GetProperty("key").GetString().Should().Be("cpu");
        body.GetProperty("attributeSchema")[0].GetProperty("filterable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task F03_US035_Brands_ListsOnlyActiveOnes()
    {
        var active = await _admin.CreateBrand();
        var hidden = await _admin.CreateBrand();
        var put = await _admin.Send(HttpMethod.Put, $"/api/v1/admin/brands/{hidden.GetProperty("id").GetGuid()}",
            new { name = hidden.GetProperty("name").GetString(), isActive = false }, "\"1\"");
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var brands = await CatalogClient.Json(await _anon.Get("/api/v1/brands"));
        var ids = brands.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();

        ids.Should().Contain(active.GetProperty("id").GetGuid()).And.NotContain(hidden.GetProperty("id").GetGuid());
    }
}

[Collection(CatalogCollection.Name)]
public class CacheTests(CatalogFactory factory)
{
    private readonly CatalogClient _admin = factory.Admin();
    private readonly CatalogClient _anon = factory.Anonymous();

    [Fact]
    public async Task F03_AC4_ChangingAPrice_InvalidatesTheRedisEntry_AndTheNextReadShowsTheNewPrice()
    {
        var p = await _admin.CreatePublished("Priced Phone", 100m, sku: "AC4-" + Guid.NewGuid().ToString("N")[..6]);
        var slug = p.GetProperty("slug").GetString()!;
        var key = CacheKeys.Product(slug);
        var sku = p.GetProperty("variants")[0].GetProperty("sku").GetString()!;

        var first = await CatalogClient.Json(await _anon.Get($"/api/v1/products/{slug}"));
        first.GetProperty("variants")[0].GetProperty("price").GetDecimal().Should().Be(100m);
        (await factory.Redis.KeyExistsAsync(key)).Should().BeTrue("a read populates cache:product:{slug}");
        (await factory.Redis.KeyTimeToLiveAsync(key))!.Value.TotalMinutes.Should().BeInRange(9, 12);

        await _admin.UpsertVariant(p, sku, 80m);

        (await factory.Redis.KeyExistsAsync(key)).Should().BeFalse("the write deleted the entry");
        var second = await CatalogClient.Json(await _anon.Get($"/api/v1/products/{slug}"));
        second.GetProperty("variants")[0].GetProperty("price").GetDecimal().Should().Be(80m);
    }

    [Fact]
    public async Task F03_AC4_RenamingTheSlug_InvalidatesTheOldKey_AndTheOldUrlStops404ing_NotServingStaleData()
    {
        var p = await _admin.CreatePublished("Renamed Phone");
        var oldSlug = p.GetProperty("slug").GetString()!;
        await _anon.Get($"/api/v1/products/{oldSlug}");
        (await factory.Redis.KeyExistsAsync(CacheKeys.Product(oldSlug))).Should().BeTrue();

        var newSlug = oldSlug + "-v2";
        var response = await _admin.Send(HttpMethod.Put, $"/api/v1/admin/products/{p.GetProperty("id").GetGuid()}",
            new
            {
                name = "Renamed Phone",
                slug = newSlug,
                description = "d",
                categoryIds = new[] { p.GetProperty("primaryCategoryId").GetGuid() },
                primaryCategoryId = p.GetProperty("primaryCategoryId").GetGuid(),
            }, $"\"{p.GetProperty("version").GetInt64()}\"");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await factory.Redis.KeyExistsAsync(CacheKeys.Product(oldSlug))).Should().BeFalse();
        (await _anon.Get($"/api/v1/products/{oldSlug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _anon.Get($"/api/v1/products/{newSlug}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F03_AC4_ArchivingAndUnpublishing_DropTheCachedPage()
    {
        var p = await _admin.CreatePublished("Cached Then Gone");
        var slug = p.GetProperty("slug").GetString()!;
        await _anon.Get($"/api/v1/products/{slug}");

        await _admin.MutateOk(p, HttpMethod.Post, "/archive");

        (await factory.Redis.KeyExistsAsync(CacheKeys.Product(slug))).Should().BeFalse();
    }

    [Fact]
    public async Task F03_NFR_ColdKeyUnderConcurrentReads_LoadsOnlyOnce()
    {
        var cache = new RedisCatalogCache(factory.Services.GetRequiredService<IConnectionMultiplexer>(), NullLogger<RedisCatalogCache>.Instance);
        var key = CacheKeys.Product("stampede-" + Guid.NewGuid().ToString("N"));
        var loads = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => cache.GetOrCreateAsync(key, TimeSpan.FromMinutes(1), async _ =>
        {
            Interlocked.Increment(ref loads);
            await Task.Delay(400);
            return new Payload("loaded");
        }, CancellationToken.None)));

        loads.Should().Be(1, "the lock makes every other reader wait for the first load");
        results.Should().OnlyContain(r => r!.Value == "loaded");
    }

    [Fact]
    public async Task F03_NFR_NotFoundResult_IsNotCached_AndWaitersFallBackToTheirOwnLoad()
    {
        var cache = new RedisCatalogCache(factory.Services.GetRequiredService<IConnectionMultiplexer>(), NullLogger<RedisCatalogCache>.Instance);
        var key = CacheKeys.Product("missing-" + Guid.NewGuid().ToString("N"));

        var result = await cache.GetOrCreateAsync<Payload>(key, TimeSpan.FromMinutes(1), _ => Task.FromResult<Payload?>(null), CancellationToken.None);

        result.Should().BeNull();
        (await factory.Redis.KeyExistsAsync(key)).Should().BeFalse();
        (await factory.Redis.KeyExistsAsync("lock:" + key)).Should().BeFalse("the lock is released");
    }

    private sealed record Payload(string Value);
}

[Collection(CatalogCollection.Name)]
public class SecurityAndPlatformTests(CatalogFactory factory)
{
    [Theory]
    [InlineData("GET", "/api/v1/admin/products")]
    [InlineData("GET", "/api/v1/admin/categories")]
    [InlineData("GET", "/api/v1/admin/brands")]
    [InlineData("POST", "/api/v1/admin/products")]
    public async Task F03_US036_AdminEndpoints_WithoutAToken_Return401(string method, string path)
    {
        var response = await factory.Anonymous().Send(new HttpMethod(method), path, method == "POST" ? new { name = "x" } : null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F03_US036_AdminEndpoints_WithoutTheCatalogWritePermission_Return403()
    {
        var customer = new CatalogClient(factory.CreateClient(), factory.Token("orders:create"));
        var supportReadOnly = new CatalogClient(factory.CreateClient(), factory.Token("users:read"));

        (await customer.Get("/api/v1/admin/products")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await supportReadOnly.Send(HttpMethod.Post, "/api/v1/admin/brands", new { name = "Nope" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task F03_PublicEndpoints_AreAnonymous()
    {
        var anon = factory.Anonymous();

        foreach (var path in new[] { "/api/v1/products", "/api/v1/brands", "/api/v1/categories/tree" })
            (await anon.Get(path)).StatusCode.Should().Be(HttpStatusCode.OK, path);
    }

    [Fact]
    public async Task F03_Errors_AreProblemDetailsWithServicePrefixedCode()
    {
        var response = await factory.Admin().Send(HttpMethod.Post, "/api/v1/admin/products", new { name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await CatalogClient.Json(response);
        body.GetProperty("code").GetString().Should().Be("CATALOG_VALIDATION_FAILED");
        body.GetProperty("errors").TryGetProperty("name", out _).Should().BeTrue();
        body.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task F03_BR09_CategoryUsedByAProduct_CannotBeDeleted_ButAnUnusedOneCan()
    {
        var admin = factory.Admin();
        var used = await admin.CreateCategory();
        await admin.CreateDraft(used.GetProperty("id").GetGuid());
        var unused = await admin.CreateCategory();

        var blocked = await admin.Send(HttpMethod.Delete, $"/api/v1/admin/categories/{used.GetProperty("id").GetGuid()}");
        var deleted = await admin.Send(HttpMethod.Delete, $"/api/v1/admin/categories/{unused.GetProperty("id").GetGuid()}");

        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CatalogClient.Code(blocked)).Should().Be("CATALOG_CATEGORY_HAS_PRODUCTS");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task F03_US034_Categories_AreLimitedToThreeLevels_AndCategoryUpdatesNeedIfMatch()
    {
        var admin = factory.Admin();
        var l1 = await admin.CreateCategory();
        var l2 = await admin.CreateCategory(parentId: l1.GetProperty("id").GetGuid());
        var l3 = await admin.CreateCategory(parentId: l2.GetProperty("id").GetGuid());

        var l4 = await admin.Send(HttpMethod.Post, "/api/v1/admin/categories", new { name = "Too deep", parentId = l3.GetProperty("id").GetGuid() });
        var stale = await admin.Send(HttpMethod.Put, $"/api/v1/admin/categories/{l1.GetProperty("id").GetGuid()}", new { name = "Renamed", isActive = true }, "\"99\"");

        l4.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CatalogClient.Code(l4)).Should().Be("CATALOG_CATEGORY_TOO_DEEP");
        stale.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task F03_Indexes_AreCreatedIdempotentlyOnStartup()
    {
        await factory.Db.EnsureIndexesAsync(CancellationToken.None); // a second run must be a no-op, not an error

        var names = (await (await factory.Db.Products.Indexes.ListAsync()).ToListAsync()).Select(i => i["name"].AsString).ToList();
        names.Should().Contain([CatalogDb.SlugIndex, CatalogDb.SkuIndex, "ix_products_category_status", "ix_products_brand_id", "ix_products_status_published"]);
        var sku = (await (await factory.Db.Products.Indexes.ListAsync()).ToListAsync()).Single(i => i["name"].AsString == CatalogDb.SkuIndex);
        sku["unique"].AsBoolean.Should().BeTrue();
        var categoryNames = (await (await factory.Db.Categories.Indexes.ListAsync()).ToListAsync()).Select(i => i["name"].AsString);
        categoryNames.Should().Contain("ux_categories_slug");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    public async Task F03_OperationalEndpoints_Respond(string path) =>
        (await factory.Anonymous().Get(path)).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task F03_Metrics_ExposeCacheRequestCountersWithoutIdLabels()
    {
        var admin = factory.Admin();
        var p = await admin.CreatePublished("Metered");
        var slug = p.GetProperty("slug").GetString();
        await factory.Anonymous().Get($"/api/v1/products/{slug}");
        await factory.Anonymous().Get($"/api/v1/products/{slug}");

        var text = await (await factory.Anonymous().Get("/metrics")).Content.ReadAsStringAsync();

        text.Should().Contain("catalog_cache_requests_total");
        text.Should().Contain("cache=\"product\"").And.Contain("result=\"hit\"").And.Contain("result=\"miss\"");
        text.Should().NotContain(slug!);
    }
}
