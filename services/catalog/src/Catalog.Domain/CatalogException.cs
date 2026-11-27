namespace Catalog.Domain;

public enum FailureKind { Validation, NotFound, Conflict, Unprocessable, PreconditionFailed, PreconditionRequired }

/// <summary>
/// Raised when a catalog business rule (BR-n) or request precondition fails. The API maps <see cref="Code"/> and
/// <see cref="Kind"/> to an RFC 7807 response. <see cref="Errors"/> carries field-level details (camelCase field paths).
/// </summary>
public sealed class CatalogException(string code, string message, FailureKind kind, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public FailureKind Kind { get; } = kind;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;

    public static CatalogException Invalid(string field, string message, string code = CatalogErrors.ValidationFailed) =>
        new(code, "One or more validation errors occurred.", FailureKind.Validation,
            new Dictionary<string, string[]> { [field] = [message] });

    public static CatalogException Invalid(IReadOnlyDictionary<string, string[]> errors, string code = CatalogErrors.ValidationFailed) =>
        new(code, "One or more validation errors occurred.", FailureKind.Validation, errors);

    public static CatalogException NotFound(string code, string message) => new(code, message, FailureKind.NotFound);
    public static CatalogException Conflict(string code, string message) => new(code, message, FailureKind.Conflict);
}

/// <summary>Error codes (docs/features/F03-product-catalog.md section 4).</summary>
public static class CatalogErrors
{
    public const string ValidationFailed = "CATALOG_VALIDATION_FAILED";
    public const string SlugTaken = "CATALOG_SLUG_TAKEN";
    public const string SkuTaken = "CATALOG_SKU_TAKEN";
    public const string InvalidAttributes = "CATALOG_INVALID_ATTRIBUTES";
    public const string PublishRequirementsNotMet = "CATALOG_PUBLISH_REQUIREMENTS_NOT_MET";
    public const string VersionMismatch = "CATALOG_VERSION_MISMATCH";
    public const string IfMatchRequired = "CATALOG_IF_MATCH_REQUIRED";
    public const string VariantHasStock = "CATALOG_VARIANT_HAS_STOCK"; // reserved: needs the inventory check (F05)
    public const string InvalidOptions = "CATALOG_INVALID_OPTIONS";
    public const string OptionsInUse = "CATALOG_OPTIONS_IN_USE";
    public const string InvalidVariant = "CATALOG_INVALID_VARIANT";
    public const string VariantCombinationTaken = "CATALOG_VARIANT_COMBINATION_TAKEN";
    public const string TooManyVariants = "CATALOG_TOO_MANY_VARIANTS";
    public const string VariantNotFound = "CATALOG_VARIANT_NOT_FOUND";
    public const string InvalidTransition = "CATALOG_INVALID_TRANSITION";
    public const string ProductNotFound = "CATALOG_PRODUCT_NOT_FOUND";
    public const string CategoryNotFound = "CATALOG_CATEGORY_NOT_FOUND";
    public const string CategoryTooDeep = "CATALOG_CATEGORY_TOO_DEEP";
    public const string CategoryHasProducts = "CATALOG_CATEGORY_HAS_PRODUCTS";
    public const string CategoryHasChildren = "CATALOG_CATEGORY_HAS_CHILDREN";
    public const string BrandNotFound = "CATALOG_BRAND_NOT_FOUND";
    public const string BrandInUse = "CATALOG_BRAND_IN_USE";
}
