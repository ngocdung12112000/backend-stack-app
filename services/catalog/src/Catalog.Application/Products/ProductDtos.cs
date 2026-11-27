using System.Text.Json;
using Catalog.Domain;

namespace Catalog.Application.Products;

/// <summary>
/// Listing card. The first four members are the original Phase 0 shape (api-client 0.1.x); the rest are additive.
/// <c>Price</c> is the lowest active variant price ("from" price).
/// </summary>
public sealed record ProductDto(
    Guid Id, string Name, decimal Price, string Currency,
    string Slug, string? BrandName, string? ImageUrl, decimal? CompareAtPrice, double RatingAverage, int RatingCount);

public sealed record BrandRefDto(Guid Id, string Name, string Slug);
public sealed record OptionDto(string Name, IReadOnlyList<string> Values);
public sealed record VariantDto(
    string Sku, IReadOnlyDictionary<string, string> Options, decimal Price, decimal? CompareAtPrice, int WeightG, string? Barcode, bool IsActive);
public sealed record ImageDto(string Url, string? ThumbUrl, string? Alt, int Sort);
public sealed record SeoDto(string? Title, string? Description);
public sealed record RatingDto(double Average, int Count);

/// <summary>Storefront product detail (published only). Price and stock shown here are display-only; checkout re-validates.</summary>
public sealed record ProductDetailDto(
    Guid Id, string Slug, string Name, BrandRefDto? Brand, IReadOnlyList<string> CategoryPath, string? Description,
    IReadOnlyDictionary<string, object?> Attributes, IReadOnlyList<OptionDto> Options, IReadOnlyList<VariantDto> Variants,
    IReadOnlyList<ImageDto> Images, IReadOnlyList<string> Tags, SeoDto? Seo, RatingDto Rating, string Currency, DateTime? PublishedAt);

/// <summary>Full admin view incl. drafts; <c>Version</c> is also sent as the ETag.</summary>
public sealed record AdminProductDto(
    Guid Id, string Slug, string Name, ProductStatus Status, BrandRefDto? Brand, IReadOnlyList<Guid> CategoryIds, Guid? PrimaryCategoryId,
    IReadOnlyList<string> CategoryPath, string? Description, IReadOnlyDictionary<string, object?> Attributes, IReadOnlyList<OptionDto> Options,
    IReadOnlyList<VariantDto> Variants, IReadOnlyList<ImageDto> Images, IReadOnlyList<string> Tags, SeoDto? Seo, RatingDto Rating,
    string Currency, long Version, DateTime CreatedAt, DateTime UpdatedAt, DateTime? PublishedAt);

public sealed record AdminProductListItemDto(
    Guid Id, string Slug, string Name, ProductStatus Status, string? BrandName, decimal? MinPrice, int VariantCount, string? ImageUrl,
    long Version, DateTime UpdatedAt);

public static class ProductMapping
{
    public static ProductDto ToCard(this Product p, string currency)
    {
        var cheapest = p.Variants.Where(v => v.IsActive).OrderBy(v => v.Price).FirstOrDefault();
        return new ProductDto(p.Id, p.Name, p.MinPrice ?? 0m, currency, p.Slug, p.Brand?.Name,
            p.Images.OrderBy(i => i.Sort).FirstOrDefault()?.Url, cheapest?.CompareAtPrice, p.Rating.Average, p.Rating.Count);
    }

    public static ProductDetailDto ToDetail(this Product p, string currency) => new(
        p.Id, p.Slug, p.Name, p.Brand?.ToDto(), p.CategoryPath, p.Description, p.Attributes,
        p.Options.Select(o => new OptionDto(o.Name, o.Values)).ToList(),
        p.Variants.Where(v => v.IsActive).Select(ToDto).ToList(),
        p.Images.OrderBy(i => i.Sort).Select(ToDto).ToList(), p.Tags,
        p.Seo is null ? null : new SeoDto(p.Seo.Title, p.Seo.Description), new RatingDto(p.Rating.Average, p.Rating.Count), currency, p.PublishedAt);

    public static AdminProductDto ToAdmin(this Product p, string currency) => new(
        p.Id, p.Slug, p.Name, p.Status, p.Brand?.ToDto(), p.CategoryIds, p.PrimaryCategoryId, p.CategoryPath, p.Description, p.Attributes,
        p.Options.Select(o => new OptionDto(o.Name, o.Values)).ToList(), p.Variants.Select(ToDto).ToList(),
        p.Images.OrderBy(i => i.Sort).Select(ToDto).ToList(), p.Tags,
        p.Seo is null ? null : new SeoDto(p.Seo.Title, p.Seo.Description), new RatingDto(p.Rating.Average, p.Rating.Count),
        currency, p.Version, p.CreatedAt, p.UpdatedAt, p.PublishedAt);

    public static AdminProductListItemDto ToListItem(this Product p) => new(
        p.Id, p.Slug, p.Name, p.Status, p.Brand?.Name, p.MinPrice, p.Variants.Count,
        p.Images.OrderBy(i => i.Sort).FirstOrDefault()?.Url, p.Version, p.UpdatedAt);

    private static BrandRefDto ToDto(this BrandRef b) => new(b.Id, b.Name, b.Slug);
    private static VariantDto ToDto(Variant v) => new(v.Sku, v.Options, v.Price, v.CompareAtPrice, v.WeightG, v.Barcode, v.IsActive);
    private static ImageDto ToDto(ImageRef i) => new(i.Url, i.ThumbUrl, i.Alt, i.Sort);

    /// <summary>JSON attribute values (text, number, bool, null) to the primitives the domain validates and Mongo stores.</summary>
    public static IReadOnlyDictionary<string, object?> ToAttributeValues(IDictionary<string, JsonElement>? json)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, el) in json ?? new Dictionary<string, JsonElement>())
        {
            result[key] = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => throw CatalogException.Invalid($"attributes.{key}", "Attribute values must be text, a number or true/false.", CatalogErrors.InvalidAttributes),
            };
        }

        return result;
    }
}
