using Catalog.Domain;

namespace Catalog.Tests;

internal static class Build
{
    public static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    public static AttributeDefinition Attr(string key, string type = AttributeTypes.Text, bool required = false, params string[] options) => new()
    {
        Key = key,
        Label = key,
        Type = type,
        Required = required,
        Options = options.Length == 0 ? null : options,
    };

    public static Category Category(string slug = "laptops", Category? parent = null, params AttributeDefinition[] schema) =>
        Domain.Category.Create(Guid.NewGuid(), slug, slug, parent, 0, schema, Now);

    public static ProductDetails Details(string name = "MacBook Air", string? slug = null, Category? category = null,
        string? description = "Light and fast", Dictionary<string, object?>? attributes = null) => new()
        {
            Name = name,
            Slug = slug ?? Slugs.From(name),
            Description = description,
            PrimaryCategoryId = category?.Id,
            CategoryIds = category is null ? [] : [category.Id],
            Attributes = attributes ?? new Dictionary<string, object?>(),
        };

    public static Product Draft(Category? category = null, string name = "MacBook Air") => Product.Create(Guid.NewGuid(), Details(name, category: category), category, Now);

    public static VariantInput Variant(string sku = "MBA-1", decimal price = 1000m, decimal? compare = null,
        Dictionary<string, string>? options = null, bool active = true) => new()
        {
            Sku = sku,
            Price = price,
            CompareAtPrice = compare,
            Options = options ?? new Dictionary<string, string>(),
            IsActive = active,
        };

    public static ImageInput Image(string url = "https://img.example.com/a.jpg") => new() { Url = url };

    public static ProductOption Option(string name, params string[] values) => new() { Name = name, Values = values };

    /// <summary>A draft that satisfies BR-05.</summary>
    public static Product Publishable()
    {
        var category = Category();
        var p = Draft(category);
        p.UpsertVariant(Variant(), Now);
        p.SetImages([Image()], Now);
        return p;
    }
}

public class SlugTests
{
    [Theory]
    [InlineData("MacBook Air M3 13\"", "macbook-air-m3-13")]
    [InlineData("  Hello,   World!!  ", "hello-world")]
    [InlineData("Áo thun Đẹp nhất", "ao-thun-dep-nhat")]
    [InlineData("Crème Brûlée Maker", "creme-brulee-maker")]
    [InlineData("Straße & Fußball", "strasse-fussball")]
    [InlineData("$$$", "item")]
    public void F03_BR01_Slug_IsLowercaseAsciiFoldedAndHyphenated(string name, string expected) =>
        Slugs.From(name).Should().Be(expected);

    [Fact]
    public void F03_BR01_Slug_IsTruncatedToMaxLengthWithoutTrailingHyphen()
    {
        var slug = Slugs.From(string.Join(' ', Enumerable.Repeat("word", 60)));

        slug.Length.Should().BeLessThanOrEqualTo(Slugs.MaxLength);
        slug.Should().NotEndWith("-");
        Slugs.IsValid(slug).Should().BeTrue();
    }

    [Fact]
    public void F03_BR01_Collision_GetsDashTwoThenDashThree()
    {
        var taken = new HashSet<string> { "macbook-air" };
        Slugs.NextAvailable("macbook-air", taken.Contains).Should().Be("macbook-air-2");

        taken.Add("macbook-air-2");
        Slugs.NextAvailable("macbook-air", taken.Contains).Should().Be("macbook-air-3");
    }

    [Fact]
    public void F03_BR01_NoCollision_KeepsTheBaseSlug() =>
        Slugs.NextAvailable("fresh", _ => false).Should().Be("fresh");

    [Theory]
    [InlineData("ok-slug", true)]
    [InlineData("Not-Ok", false)]
    [InlineData("double--dash", false)]
    [InlineData("-leading", false)]
    [InlineData("", false)]
    public void F03_BR01_IsValid(string slug, bool expected) => Slugs.IsValid(slug).Should().Be(expected);
}

public class ProductVariantTests
{
    [Fact]
    public void F03_BR03_Variant_MustCoverEveryProductOption()
    {
        var p = Build.Draft();
        p.SetOptions([Build.Option("Color", "Red", "Blue"), Build.Option("Size", "S", "M")], Build.Now);

        var act = () => p.UpsertVariant(Build.Variant(options: new() { ["Color"] = "Red" }), Build.Now);

        act.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.InvalidVariant);
    }

    [Fact]
    public void F03_BR03_Variant_MustUseAValidOptionValue()
    {
        var p = Build.Draft();
        p.SetOptions([Build.Option("Color", "Red", "Blue")], Build.Now);

        var act = () => p.UpsertVariant(Build.Variant(options: new() { ["Color"] = "Green" }), Build.Now);

        act.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.InvalidVariant);
    }

    [Fact]
    public void F03_BR03_TwoVariants_CannotShareAnOptionCombination()
    {
        var p = Build.Draft();
        p.SetOptions([Build.Option("Color", "Red", "Blue")], Build.Now);
        p.UpsertVariant(Build.Variant("RED-1", options: new() { ["Color"] = "Red" }), Build.Now);

        var act = () => p.UpsertVariant(Build.Variant("RED-2", options: new() { ["Color"] = "Red" }), Build.Now);

        act.Should().Throw<CatalogException>().Which.Should().Match<CatalogException>(e =>
            e.Code == CatalogErrors.VariantCombinationTaken && e.Kind == FailureKind.Conflict);
    }

    [Fact]
    public void F03_BR03_UpsertingTheSameSku_ReplacesInsteadOfClashing()
    {
        var p = Build.Draft();
        p.SetOptions([Build.Option("Color", "Red")], Build.Now);
        p.UpsertVariant(Build.Variant("RED-1", 10m, options: new() { ["Color"] = "Red" }), Build.Now).Should().BeTrue();

        p.UpsertVariant(Build.Variant("red-1", 12m, options: new() { ["Color"] = "Red" }), Build.Now).Should().BeFalse();

        p.Variants.Should().ContainSingle().Which.Price.Should().Be(12m);
    }

    [Fact]
    public void F03_BR03_ChangingOptions_ThatWouldOrphanVariants_IsRejected()
    {
        var p = Build.Draft();
        p.SetOptions([Build.Option("Color", "Red", "Blue")], Build.Now);
        p.UpsertVariant(Build.Variant("RED-1", options: new() { ["Color"] = "Red" }), Build.Now);

        var act = () => p.SetOptions([Build.Option("Color", "Blue")], Build.Now);

        act.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.OptionsInUse);
    }

    [Fact]
    public void F03_Options_AreLimitedToThreeWithTwentyValuesEach()
    {
        var p = Build.Draft();

        var tooMany = () => p.SetOptions([Build.Option("A", "1"), Build.Option("B", "1"), Build.Option("C", "1"), Build.Option("D", "1")], Build.Now);
        var tooManyValues = () => p.SetOptions([Build.Option("A", Enumerable.Range(1, 21).Select(i => $"v{i}").ToArray())], Build.Now);
        var ok = () => p.SetOptions([Build.Option("A", Enumerable.Range(1, 20).Select(i => $"v{i}").ToArray())], Build.Now);

        tooMany.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.InvalidOptions);
        tooManyValues.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.InvalidOptions);
        ok.Should().NotThrow();
    }

    [Fact]
    public void F03_Variants_AreLimitedToOneHundred()
    {
        var p = Build.Draft();
        p.SetOptions([Build.Option("N", Enumerable.Range(0, 20).Select(i => $"n{i}").ToArray()), Build.Option("M", "a", "b", "c", "d", "e", "f")], Build.Now);
        var n = 0;
        foreach (var a in Enumerable.Range(0, 20))
            foreach (var b in new[] { "a", "b", "c", "d", "e" })
                p.UpsertVariant(Build.Variant($"SKU-{n++}", options: new() { ["N"] = $"n{a}", ["M"] = b }), Build.Now);
        p.Variants.Should().HaveCount(100);

        var act = () => p.UpsertVariant(Build.Variant("SKU-X", options: new() { ["N"] = "n0", ["M"] = "f" }), Build.Now);

        act.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.TooManyVariants);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(100, 99)]
    public void F03_BR06_CompareAtPrice_MustBeGreaterThanPrice(double price, double compare)
    {
        var p = Build.Draft();

        var act = () => p.UpsertVariant(Build.Variant(price: (decimal)price, compare: (decimal)compare), Build.Now);

        act.Should().Throw<CatalogException>().Which.Errors.Should().ContainKey("compareAtPrice");
    }

    [Fact]
    public void F03_BR06_CompareAtPrice_AbovePrice_IsAccepted()
    {
        var p = Build.Draft();

        p.UpsertVariant(Build.Variant(price: 100m, compare: 120m), Build.Now);

        p.Variants.Single().CompareAtPrice.Should().Be(120m);
    }

    [Fact]
    public void F03_Price_IsStoredWithTwoDecimalsAtMost()
    {
        var act = () => Build.Draft().UpsertVariant(Build.Variant(price: 9.999m), Build.Now);

        act.Should().Throw<CatalogException>().Which.Errors.Should().ContainKey("price");
    }

    [Fact]
    public void F03_MinPrice_TracksTheCheapestActiveVariant()
    {
        var p = Build.Draft();
        p.SetOptions([Build.Option("Size", "S", "M", "L")], Build.Now);
        p.UpsertVariant(Build.Variant("SZ-S", 30m, options: new() { ["Size"] = "S" }), Build.Now);
        p.UpsertVariant(Build.Variant("SZ-M", 20m, options: new() { ["Size"] = "M" }, active: false), Build.Now);
        p.UpsertVariant(Build.Variant("SZ-L", 25m, options: new() { ["Size"] = "L" }), Build.Now);

        p.MinPrice.Should().Be(25m);

        p.RemoveVariant("sz-l", Build.Now);
        p.MinPrice.Should().Be(30m);
    }

    [Fact]
    public void F03_RemoveVariant_Unknown_IsNotFound()
    {
        var act = () => Build.Draft().RemoveVariant("NOPE", Build.Now);

        act.Should().Throw<CatalogException>().Which.Kind.Should().Be(FailureKind.NotFound);
    }

    [Fact]
    public void F03_BR08_EveryChange_BumpsTheVersion()
    {
        var p = Build.Draft();
        p.Version.Should().Be(1);

        p.UpsertVariant(Build.Variant(), Build.Now);
        p.SetImages([Build.Image()], Build.Now);
        p.Version.Should().Be(3);
    }
}

public class ProductPublishTests
{
    [Fact]
    public void F03_AC1_Publish_WithNoImages_ListsTheUnmetRequirement()
    {
        var p = Build.Draft(Build.Category());
        p.UpsertVariant(Build.Variant(), Build.Now);

        var act = () => p.Publish(Build.Now);

        var e = act.Should().Throw<CatalogException>().Which;
        e.Code.Should().Be(CatalogErrors.PublishRequirementsNotMet);
        e.Kind.Should().Be(FailureKind.Unprocessable);
        e.Errors.Should().ContainKey("images").And.NotContainKey("variants");
        p.Status.Should().Be(ProductStatus.Draft);
    }

    [Fact]
    public void F03_BR05_Publish_ReportsEveryMissingRequirement()
    {
        var p = Product.Create(Guid.NewGuid(), Build.Details(description: null), null, Build.Now);

        var act = () => p.Publish(Build.Now);

        act.Should().Throw<CatalogException>().Which.Errors!.Keys.Should()
            .BeEquivalentTo("variants", "images", "description", "primaryCategoryId");
    }

    [Fact]
    public void F03_BR05_Publish_NeedsAnActiveVariantWithAPriceAboveZero()
    {
        var p = Build.Publishable();
        p.UpsertVariant(Build.Variant(price: 0m), Build.Now);

        var act = () => p.Publish(Build.Now);

        act.Should().Throw<CatalogException>().Which.Errors.Should().ContainKey("variants");
    }

    [Fact]
    public void F03_BR05_Publish_WhenRequirementsAreMet_SetsStatusAndTimestamp()
    {
        var p = Build.Publishable();

        p.Publish(Build.Now);

        p.Status.Should().Be(ProductStatus.Published);
        p.PublishedAt.Should().Be(Build.Now);
    }

    [Fact]
    public void F03_BR05_PublishedProduct_CannotLoseItsLastImage()
    {
        var p = Build.Publishable();
        p.Publish(Build.Now);

        var act = () => p.SetImages([], Build.Now);

        act.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.PublishRequirementsNotMet);
    }

    [Fact]
    public void F03_BR05_PublishedProduct_CannotLoseItsLastActiveVariant()
    {
        var p = Build.Publishable();
        p.Publish(Build.Now);

        var act = () => p.RemoveVariant("MBA-1", Build.Now);

        act.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.PublishRequirementsNotMet);
    }

    [Fact]
    public void F03_Status_Transitions()
    {
        var p = Build.Publishable();
        var publishTwice = () => { p.Publish(Build.Now); p.Publish(Build.Now); };
        publishTwice.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.InvalidTransition);

        p.Unpublish(Build.Now);
        p.Status.Should().Be(ProductStatus.Draft);
        var unpublishDraft = () => p.Unpublish(Build.Now);
        unpublishDraft.Should().Throw<CatalogException>();

        p.Archive(Build.Now);
        p.Status.Should().Be(ProductStatus.Archived);
        var republish = () => p.Publish(Build.Now);
        republish.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.InvalidTransition);
        var archiveAgain = () => p.Archive(Build.Now);
        archiveAgain.Should().Throw<CatalogException>();
    }
}

public class ProductAttributeTests
{
    private static Category Laptops() => Build.Category("laptops", null,
        Build.Attr("cpu", AttributeTypes.Text, required: true),
        Build.Attr("ramGb", AttributeTypes.Number),
        Build.Attr("touch", AttributeTypes.Bool),
        Build.Attr("color", AttributeTypes.Enum, false, "Silver", "Black"));

    [Fact]
    public void F03_AC5_MatchingAttributes_AreAccepted()
    {
        var c = Laptops();
        var attributes = new Dictionary<string, object?> { ["cpu"] = "M3", ["ramGb"] = 16L, ["touch"] = false, ["color"] = "Silver" };

        var p = Product.Create(Guid.NewGuid(), Build.Details(category: c, attributes: attributes), c, Build.Now);

        p.Attributes.Should().HaveCount(4);
    }

    [Fact]
    public void F03_AC5_WrongTypeUnknownKeyBadEnumAndMissingRequired_AreFieldLevelErrors()
    {
        var c = Laptops();
        var attributes = new Dictionary<string, object?>
        {
            ["ramGb"] = "sixteen",
            ["touch"] = "yes",
            ["color"] = "Pink",
            ["weirdo"] = 1,
        };

        var act = () => Product.Create(Guid.NewGuid(), Build.Details(category: c, attributes: attributes), c, Build.Now);

        var e = act.Should().Throw<CatalogException>().Which;
        e.Code.Should().Be(CatalogErrors.InvalidAttributes);
        e.Errors!.Keys.Should().BeEquivalentTo("attributes.cpu", "attributes.ramGb", "attributes.touch", "attributes.color", "attributes.weirdo");
    }

    [Fact]
    public void F03_BR04_AttributesWithoutACategory_AreRejected()
    {
        var act = () => Product.Create(Guid.NewGuid(), Build.Details(attributes: new() { ["cpu"] = "M3" }), null, Build.Now);

        act.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.InvalidAttributes);
    }

    [Fact]
    public void F03_BR04_UpdatingDetails_RevalidatesAgainstTheNewPrimaryCategory()
    {
        var laptops = Laptops();
        var phones = Build.Category("phones", null, Build.Attr("os", AttributeTypes.Text, required: true));
        var p = Product.Create(Guid.NewGuid(), Build.Details(category: laptops, attributes: new() { ["cpu"] = "M3" }), laptops, Build.Now);

        var act = () => p.UpdateDetails(Build.Details(category: phones, attributes: new() { ["cpu"] = "M3" }), phones, Build.Now);

        act.Should().Throw<CatalogException>().Which.Errors.Should().ContainKey("attributes.os").And.ContainKey("attributes.cpu");
    }

    [Fact]
    public void F03_BR04_NullAttributeValues_AreTreatedAsAbsent()
    {
        var c = Laptops();

        var p = Product.Create(Guid.NewGuid(), Build.Details(category: c, attributes: new() { ["cpu"] = "M3", ["ramGb"] = null }), c, Build.Now);

        p.Attributes.Should().NotContainKey("ramGb");
    }
}

public class CategoryTests
{
    [Fact]
    public void F03_US034_Categories_AreAtMostThreeLevelsDeep()
    {
        var l1 = Build.Category("electronics");
        var l2 = Build.Category("computers", l1);
        var l3 = Build.Category("laptops", l2);

        l3.Level.Should().Be(3);
        l3.Path.Should().Equal("electronics", "computers", "laptops");

        var act = () => Build.Category("gaming-laptops", l3);
        act.Should().Throw<CatalogException>().Which.Code.Should().Be(CatalogErrors.CategoryTooDeep);
    }

    [Fact]
    public void F03_CategorySchema_RejectsDuplicateKeysAndEnumsWithoutOptions()
    {
        var act = () => Category.Create(Guid.NewGuid(), "X", "x", null, 0,
            [Build.Attr("cpu"), Build.Attr("cpu"), Build.Attr("color", AttributeTypes.Enum), Build.Attr("Bad Key")], Build.Now);

        var errors = act.Should().Throw<CatalogException>().Which.Errors!;
        errors.Keys.Should().Contain(["attributeSchema[1].key", "attributeSchema[2].options", "attributeSchema[3].key"]);
    }

    [Fact]
    public void F03_Category_UpdateBumpsVersionAndKeepsSlugAndPath()
    {
        var parent = Build.Category("electronics");
        var c = Build.Category("computers", parent);

        c.Update("Computers & PCs", 5, false, [], Build.Now);

        c.Version.Should().Be(2);
        c.Slug.Should().Be("computers");
        c.Path.Should().Equal("electronics", "computers");
        c.IsActive.Should().BeFalse();
    }

    [Fact]
    public void F03_Brand_RequiresNameAndValidSlugAndLogoUrl()
    {
        var act = () => Brand.Create(Guid.NewGuid(), "", "Bad Slug", "not-a-url", true, Build.Now);

        act.Should().Throw<CatalogException>().Which.Errors!.Keys.Should().BeEquivalentTo("name", "slug", "logoUrl");
    }
}
