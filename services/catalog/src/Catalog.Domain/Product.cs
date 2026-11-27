using System.Text.RegularExpressions;

namespace Catalog.Domain;

public sealed class BrandRef
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
}

public sealed class ProductOption
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Values { get; init; } = [];
}

public sealed class Variant
{
    public string Sku { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    public decimal Price { get; init; }
    public decimal? CompareAtPrice { get; init; }
    public int WeightG { get; init; }
    public string? Barcode { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed class ImageRef
{
    public string Url { get; init; } = string.Empty;
    public string? ThumbUrl { get; init; }
    public string? Alt { get; init; }
    public int Sort { get; init; }
}

public sealed class Seo
{
    public string? Title { get; init; }
    public string? Description { get; init; }
}

public sealed class Rating
{
    public double Average { get; init; }
    public int Count { get; init; }
}

/// <summary>The editable "general" part of a product (PUT /admin/products/{id}). Slug null = keep or generate.</summary>
public sealed class ProductDetails
{
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? Description { get; init; }
    public BrandRef? Brand { get; init; }
    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];
    public Guid? PrimaryCategoryId { get; init; }
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Seo? Seo { get; init; }
}

public sealed class VariantInput
{
    public string Sku { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    public decimal Price { get; init; }
    public decimal? CompareAtPrice { get; init; }
    public int WeightG { get; init; }
    public string? Barcode { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed class ImageInput
{
    public string Url { get; init; } = string.Empty;
    public string? ThumbUrl { get; init; }
    public string? Alt { get; init; }
}

/// <summary>
/// Product aggregate root (F03 section 2). Every mutating method enforces its business rules, bumps <see cref="Version"/>
/// (optimistic concurrency, BR-08) and throws <see cref="CatalogException"/> on a violation. A published product is
/// re-checked against the publish requirements (BR-05) after each change, so it can never drift into an invalid state.
/// </summary>
public sealed class Product
{
    public const int MaxOptions = 3;
    public const int MaxOptionValues = 20;
    public const int MaxVariants = 100;
    public const int MaxImages = 20;
    public const int MaxTags = 20;
    public const int CurrentSchemaVersion = 1;

    private static readonly Regex SkuPattern = new("^[A-Z0-9][A-Z0-9._-]{1,63}$", RegexOptions.Compiled);

    public Guid Id { get; private set; }
    public string Slug { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public BrandRef? Brand { get; private set; }
    public IReadOnlyList<Guid> CategoryIds { get; private set; } = [];
    public Guid? PrimaryCategoryId { get; private set; }
    /// <summary>Breadcrumb: slugs from the root to the primary category. A snapshot refreshed whenever the product is saved.</summary>
    public IReadOnlyList<string> CategoryPath { get; private set; } = [];
    public string? Description { get; private set; }
    public IReadOnlyDictionary<string, object?> Attributes { get; private set; } = new Dictionary<string, object?>();
    public IReadOnlyList<ProductOption> Options { get; private set; } = [];
    public IReadOnlyList<Variant> Variants { get; private set; } = [];
    public IReadOnlyList<ImageRef> Images { get; private set; } = [];
    public IReadOnlyList<string> Tags { get; private set; } = [];
    public Seo? Seo { get; private set; }
    public ProductStatus Status { get; private set; } = ProductStatus.Draft;
    public Rating Rating { get; private set; } = new();
    /// <summary>Lowest active variant price. Stored so listings can sort and filter on it; recomputed on every variant change.</summary>
    public decimal? MinPrice { get; private set; }
    public long Version { get; private set; }
    public int SchemaVersion { get; private set; } = CurrentSchemaVersion;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? PublishedAt { get; private set; }

    private Product() { }

    public static Product Create(Guid id, ProductDetails details, Category? primaryCategory, DateTime now)
    {
        var p = new Product { Id = id, Version = 1, CreatedAt = now, UpdatedAt = now };
        p.ApplyDetails(details, primaryCategory);
        return p;
    }

    /// <summary>Replaces the general fields. The slug is editable (BR-01); uniqueness is checked by the caller and the unique index.</summary>
    public void UpdateDetails(ProductDetails details, Category? primaryCategory, DateTime now)
    {
        ApplyDetails(details, primaryCategory);
        EnsurePublishableWhenPublished();
        Touch(now);
    }

    public void SetOptions(IReadOnlyList<ProductOption> options, DateTime now)
    {
        var errors = new Dictionary<string, string[]>();
        if (options.Count > MaxOptions) errors["options"] = [$"At most {MaxOptions} options."];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            var path = $"options[{i}]";
            if (string.IsNullOrWhiteSpace(o.Name) || o.Name.Trim().Length > 40 || o.Name.Contains('.') || o.Name.Contains('$'))
                errors[$"{path}.name"] = ["Name is required (max 40 characters, no '.' or '$')."];
            else if (!names.Add(o.Name.Trim())) errors[$"{path}.name"] = ["Duplicate option name."];
            if (o.Values.Count == 0) errors[$"{path}.values"] = ["At least one value."];
            else if (o.Values.Count > MaxOptionValues) errors[$"{path}.values"] = [$"At most {MaxOptionValues} values."];
            else if (o.Values.Any(v => string.IsNullOrWhiteSpace(v) || v.Trim().Length > 60)) errors[$"{path}.values"] = ["Values must be non-empty (max 60 characters)."];
            else if (o.Values.Select(v => v.Trim()).Distinct().Count() != o.Values.Count) errors[$"{path}.values"] = ["Duplicate values."];
        }

        if (errors.Count > 0) throw CatalogException.Invalid(errors, CatalogErrors.InvalidOptions);

        var normalized = options.Select(o => new ProductOption { Name = o.Name.Trim(), Values = o.Values.Select(v => v.Trim()).ToList() }).ToList();
        var broken = Variants.Where(v => !MatchesOptions(v.Options, normalized)).Select(v => v.Sku).ToList();
        if (broken.Count > 0)
            throw new CatalogException(CatalogErrors.OptionsInUse,
                $"These options would invalidate existing variants ({string.Join(", ", broken)}). Update or remove the variants first.",
                FailureKind.Conflict);

        Options = normalized;
        Touch(now);
    }

    /// <summary>Creates the variant or replaces the one with the same SKU. Returns true when it was created.</summary>
    public bool UpsertVariant(VariantInput input, DateTime now)
    {
        var sku = NormalizeSku(input.Sku);
        var errors = new Dictionary<string, string[]>();
        if (!SkuPattern.IsMatch(sku)) errors["sku"] = ["SKU must be 2-64 characters: letters, digits, '.', '_' or '-'."];
        if (input.Price < 0 || input.Price > 1_000_000_000m || decimal.Round(input.Price, 2) != input.Price)
            errors["price"] = ["Price must be between 0 and 1,000,000,000 with at most 2 decimals."];
        if (input.CompareAtPrice is { } compare)
        {
            if (decimal.Round(compare, 2) != compare) errors["compareAtPrice"] = ["Compare-at price can have at most 2 decimals."];
            else if (compare <= input.Price) errors["compareAtPrice"] = ["Compare-at price must be greater than the price (BR-06)."]; // BR-06
        }
        if (input.WeightG < 0) errors["weightG"] = ["Weight cannot be negative."];
        if (input.Barcode is { Length: > 64 }) errors["barcode"] = ["Barcode is too long."];
        if (errors.Count > 0) throw CatalogException.Invalid(errors);

        // BR-03: the variant must cover every product option with a valid value.
        if (!MatchesOptions(input.Options, Options))
            throw new CatalogException(CatalogErrors.InvalidVariant,
                "Variant options must cover every product option with one of its values.", FailureKind.Validation,
                new Dictionary<string, string[]> { ["options"] = [$"Expected exactly: {(Options.Count == 0 ? "no options" : string.Join(", ", Options.Select(o => o.Name)))}."] });

        // BR-03: no two variants may share an option combination.
        var clash = Variants.FirstOrDefault(v => v.Sku != sku && SameCombination(v.Options, input.Options));
        if (clash is not null)
            throw CatalogException.Conflict(CatalogErrors.VariantCombinationTaken, $"Variant {clash.Sku} already uses this option combination.");

        var existing = Variants.Any(v => v.Sku == sku);
        if (!existing && Variants.Count >= MaxVariants)
            throw new CatalogException(CatalogErrors.TooManyVariants, $"A product can have at most {MaxVariants} variants.", FailureKind.Conflict);

        var variant = new Variant
        {
            Sku = sku,
            Options = new Dictionary<string, string>(input.Options),
            Price = input.Price,
            CompareAtPrice = input.CompareAtPrice,
            WeightG = input.WeightG,
            Barcode = input.Barcode,
            IsActive = input.IsActive,
        };
        // TODO(F03 phase 2, BR-10): emit catalog.product-price-changed.v1 per SKU here once the outbox exists.
        Variants = existing ? Variants.Select(v => v.Sku == sku ? variant : v).ToList() : [.. Variants, variant];
        RecalculateMinPrice();
        EnsurePublishableWhenPublished();
        Touch(now);
        return !existing;
    }

    /// <summary>
    /// Removes a variant. TODO(F05): reject with CATALOG_VARIANT_HAS_STOCK when inventory still holds stock for the SKU
    /// (inventory.CheckStock over gRPC); the check cannot exist before the inventory service does.
    /// </summary>
    public void RemoveVariant(string sku, DateTime now)
    {
        sku = NormalizeSku(sku);
        if (Variants.All(v => v.Sku != sku))
            throw CatalogException.NotFound(CatalogErrors.VariantNotFound, $"Variant {sku} does not exist on this product.");
        Variants = Variants.Where(v => v.Sku != sku).ToList();
        RecalculateMinPrice();
        EnsurePublishableWhenPublished();
        Touch(now);
    }

    public void SetImages(IReadOnlyList<ImageInput> images, DateTime now)
    {
        var errors = new Dictionary<string, string[]>();
        if (images.Count > MaxImages) errors["images"] = [$"At most {MaxImages} images."];
        for (var i = 0; i < images.Count && i < 100; i++)
        {
            if (!Validation.IsHttpUrl(images[i].Url)) errors[$"images[{i}].url"] = ["Must be an absolute http(s) URL."];
            if (images[i].ThumbUrl is { } t && !Validation.IsHttpUrl(t)) errors[$"images[{i}].thumbUrl"] = ["Must be an absolute http(s) URL."];
            if (images[i].Alt is { Length: > 200 }) errors[$"images[{i}].alt"] = ["Alt text is too long (max 200)."];
        }

        if (errors.Count > 0) throw CatalogException.Invalid(errors);
        Images = images.Select((img, i) => new ImageRef { Url = img.Url, ThumbUrl = img.ThumbUrl, Alt = img.Alt, Sort = i }).ToList();
        EnsurePublishableWhenPublished();
        Touch(now);
    }

    /// <summary>BR-05. Draft to Published. Throws with the list of unmet requirements.</summary>
    public void Publish(DateTime now)
    {
        if (Status != ProductStatus.Draft)
            throw CatalogException.Conflict(CatalogErrors.InvalidTransition, $"A {Status} product cannot be published.");
        EnsurePublishable();
        Status = ProductStatus.Published;
        PublishedAt ??= now;
        Touch(now);
    }

    public void Unpublish(DateTime now)
    {
        if (Status != ProductStatus.Published)
            throw CatalogException.Conflict(CatalogErrors.InvalidTransition, $"A {Status} product cannot be unpublished.");
        Status = ProductStatus.Draft;
        Touch(now);
    }

    /// <summary>BR-07: archived products leave the storefront but stay resolvable by id. Archiving is final.</summary>
    public void Archive(DateTime now)
    {
        if (Status == ProductStatus.Archived)
            throw CatalogException.Conflict(CatalogErrors.InvalidTransition, "The product is already archived.");
        Status = ProductStatus.Archived;
        Touch(now);
    }

    public static string NormalizeSku(string sku) => (sku ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Unmet publish requirements (BR-05), keyed by field. Empty when the product can be published.</summary>
    public IReadOnlyDictionary<string, string[]> PublishProblems()
    {
        var problems = new Dictionary<string, string[]>();
        if (!Variants.Any(v => v.IsActive && v.Price > 0)) problems["variants"] = ["At least one active variant with a price above 0 is required."];
        if (Images.Count == 0) problems["images"] = ["At least one image is required."];
        if (string.IsNullOrWhiteSpace(Description)) problems["description"] = ["A description is required."];
        if (PrimaryCategoryId is null) problems["primaryCategoryId"] = ["A primary category is required."];
        return problems;
    }

    private void EnsurePublishable()
    {
        var problems = PublishProblems();
        if (problems.Count > 0)
            throw new CatalogException(CatalogErrors.PublishRequirementsNotMet,
                "The product does not meet the publish requirements.", FailureKind.Unprocessable, problems);
    }

    private void EnsurePublishableWhenPublished()
    {
        if (Status == ProductStatus.Published) EnsurePublishable();
    }

    private void ApplyDetails(ProductDetails d, Category? primary)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Trim().Length > 200) errors["name"] = ["Name is required (max 200 characters)."];
        if (!Slugs.IsValid(d.Slug)) errors["slug"] = ["Slug must be lowercase letters, digits and single hyphens."];
        if (d.Description is { Length: > 20000 }) errors["description"] = ["Description is too long (max 20000)."];
        if (d.Tags.Count > MaxTags || d.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 50)) errors["tags"] = [$"At most {MaxTags} tags of up to 50 characters."];
        if (d.Seo is { Title.Length: > 200 } or { Description.Length: > 500 }) errors["seo"] = ["SEO title (max 200) or description (max 500) is too long."];
        if (d.PrimaryCategoryId is not null && primary?.Id != d.PrimaryCategoryId) errors["primaryCategoryId"] = ["Primary category was not resolved."];
        if (errors.Count > 0) throw CatalogException.Invalid(errors);

        // BR-04: attributes against the primary category's schema.
        var attributeErrors = primary is null
            ? d.Attributes.ToDictionary(kv => $"attributes.{kv.Key}", _ => new[] { "Choose a primary category before setting attributes." })
            : AttributeSchemaValidator.ValidateValues(primary.AttributeSchema, d.Attributes);
        if (attributeErrors.Count > 0)
            throw new CatalogException(CatalogErrors.InvalidAttributes,
                "The attributes do not match the primary category's schema.", FailureKind.Validation, attributeErrors);

        Name = d.Name.Trim();
        Slug = d.Slug;
        Description = d.Description;
        Brand = d.Brand;
        PrimaryCategoryId = primary?.Id;
        CategoryIds = (primary is null ? d.CategoryIds : d.CategoryIds.Append(primary.Id)).Distinct().ToList();
        CategoryPath = primary?.Path ?? [];
        Attributes = d.Attributes.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
        Tags = d.Tags.Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Seo = d.Seo;
    }

    private void RecalculateMinPrice() =>
        MinPrice = Variants.Where(v => v.IsActive).Select(v => (decimal?)v.Price).Min();

    private void Touch(DateTime now)
    {
        Version++;
        UpdatedAt = now;
    }

    /// <summary>BR-03: the options map must have exactly one valid value for each product option.</summary>
    private static bool MatchesOptions(IReadOnlyDictionary<string, string> chosen, IReadOnlyList<ProductOption> options) =>
        chosen.Count == options.Count && options.All(o => chosen.TryGetValue(o.Name, out var v) && o.Values.Contains(v));

    private static bool SameCombination(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
