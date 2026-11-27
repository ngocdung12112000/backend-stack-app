using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Catalog.Domain;

public static class AttributeTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Bool = "bool";
    public const string Enum = "enum";
    public static readonly IReadOnlyList<string> All = [Text, Number, Bool, Enum];
}

public sealed class AttributeDefinition
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Type { get; init; } = AttributeTypes.Text;
    public string? Unit { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public bool Filterable { get; init; }
    public bool Required { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProductStatus { Draft, Published, Archived }

public static class Validation
{
    public static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public sealed class Brand
{
    public const int CurrentSchemaVersion = 1;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string? LogoUrl { get; private set; }
    public bool IsActive { get; private set; }
    public long Version { get; private set; }
    public int SchemaVersion { get; private set; } = CurrentSchemaVersion;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Brand() { }

    public static Brand Create(Guid id, string name, string slug, string? logoUrl, bool isActive, DateTime now)
    {
        var b = new Brand { Id = id, Version = 1, CreatedAt = now };
        b.Apply(name, slug, logoUrl, isActive, now);
        return b;
    }

    public void Update(string name, string slug, string? logoUrl, bool isActive, DateTime now)
    {
        Apply(name, slug, logoUrl, isActive, now);
        Version++;
    }

    private void Apply(string name, string slug, string? logoUrl, bool isActive, DateTime now)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) errors["name"] = ["Name is required (max 100 characters)."];
        if (!Slugs.IsValid(slug)) errors["slug"] = ["Slug must be lowercase letters, digits and single hyphens."];
        if (!string.IsNullOrEmpty(logoUrl) && !Validation.IsHttpUrl(logoUrl)) errors["logoUrl"] = ["Logo must be an absolute http(s) URL."];
        if (errors.Count > 0) throw CatalogException.Invalid(errors);

        Name = name.Trim();
        Slug = slug;
        LogoUrl = string.IsNullOrEmpty(logoUrl) ? null : logoUrl;
        IsActive = isActive;
        UpdatedAt = now;
    }
}

public sealed class Category
{
    public const int MaxLevel = 3;
    public const int MaxAttributes = 50;
    public const int CurrentSchemaVersion = 1;

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    /// <summary>Immutable after creation: product breadcrumbs and descendants' <see cref="Path"/> embed it.</summary>
    public string Slug { get; private set; } = string.Empty;
    public Guid? ParentId { get; private set; }
    /// <summary>Slugs from the root down to this category (inclusive). A category's descendants are the ones whose path contains its slug.</summary>
    public IReadOnlyList<string> Path { get; private set; } = [];
    public int Level { get; private set; }
    public int Sort { get; private set; }
    public bool IsActive { get; private set; }
    public IReadOnlyList<AttributeDefinition> AttributeSchema { get; private set; } = [];
    public long Version { get; private set; }
    public int SchemaVersion { get; private set; } = CurrentSchemaVersion;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Category() { }

    public static Category Create(Guid id, string name, string slug, Category? parent, int sort,
        IReadOnlyList<AttributeDefinition>? schema, DateTime now)
    {
        if (!Slugs.IsValid(slug)) throw CatalogException.Invalid("slug", "Slug must be lowercase letters, digits and single hyphens.");
        var level = (parent?.Level ?? 0) + 1;
        if (level > MaxLevel)
            throw new CatalogException(CatalogErrors.CategoryTooDeep, $"Categories can be at most {MaxLevel} levels deep.", FailureKind.Validation,
                new Dictionary<string, string[]> { ["parentId"] = [$"Maximum depth of {MaxLevel} levels exceeded."] });

        var c = new Category
        {
            Id = id,
            Slug = slug,
            ParentId = parent?.Id,
            Level = level,
            Version = 1,
            CreatedAt = now,
            Path = [.. parent?.Path ?? [], slug],
        };
        c.Apply(name, sort, true, schema, now);
        return c;
    }

    public void Update(string name, int sort, bool isActive, IReadOnlyList<AttributeDefinition>? schema, DateTime now)
    {
        Apply(name, sort, isActive, schema, now);
        Version++;
    }

    private void Apply(string name, int sort, bool isActive, IReadOnlyList<AttributeDefinition>? schema, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw CatalogException.Invalid("name", "Name is required (max 100 characters).");
        var normalized = AttributeSchemaValidator.ValidateSchema(schema ?? []);
        Name = name.Trim();
        Sort = sort;
        IsActive = isActive;
        AttributeSchema = normalized;
        UpdatedAt = now;
    }
}

public static class AttributeSchemaValidator
{
    private static readonly Regex KeyPattern = new("^[a-z][a-zA-Z0-9_]{0,39}$", RegexOptions.Compiled);

    /// <summary>Checks the schema definition itself (admin input) and returns a normalised copy.</summary>
    public static IReadOnlyList<AttributeDefinition> ValidateSchema(IReadOnlyList<AttributeDefinition> schema)
    {
        var errors = new Dictionary<string, string[]>();
        if (schema.Count > Category.MaxAttributes) errors["attributeSchema"] = [$"At most {Category.MaxAttributes} attributes."];
        var seen = new HashSet<string>();
        for (var i = 0; i < schema.Count; i++)
        {
            var a = schema[i];
            var path = $"attributeSchema[{i}]";
            if (!KeyPattern.IsMatch(a.Key ?? "")) errors[$"{path}.key"] = ["Key must start with a lowercase letter and contain only letters, digits and underscores."];
            else if (!seen.Add(a.Key!)) errors[$"{path}.key"] = ["Duplicate key."];
            if (string.IsNullOrWhiteSpace(a.Label)) errors[$"{path}.label"] = ["Label is required."];
            if (!AttributeTypes.All.Contains(a.Type)) errors[$"{path}.type"] = [$"Type must be one of {string.Join(", ", AttributeTypes.All)}."];
            else if (a.Type == AttributeTypes.Enum && (a.Options is null || a.Options.Count == 0 || a.Options.Any(string.IsNullOrWhiteSpace)))
                errors[$"{path}.options"] = ["Enum attributes need at least one non-empty option."];
        }

        if (errors.Count > 0) throw CatalogException.Invalid(errors);
        return schema.Select(a => new AttributeDefinition
        {
            Key = a.Key,
            Label = a.Label.Trim(),
            Type = a.Type,
            Unit = a.Unit,
            Options = a.Type == AttributeTypes.Enum ? a.Options!.Distinct().ToList() : null,
            Filterable = a.Filterable,
            Required = a.Required,
        }).ToList();
    }

    /// <summary>BR-04 / AC-5: product attributes must match the primary category's schema. Returns field errors keyed <c>attributes.&lt;key&gt;</c>.</summary>
    public static Dictionary<string, string[]> ValidateValues(IReadOnlyList<AttributeDefinition> schema, IReadOnlyDictionary<string, object?> values)
    {
        var errors = new Dictionary<string, string[]>();
        var byKey = schema.ToDictionary(a => a.Key);

        foreach (var (key, value) in values)
        {
            if (!byKey.TryGetValue(key, out var def)) { errors[$"attributes.{key}"] = ["Not defined by the category's attribute schema."]; continue; }
            if (value is null) continue; // treated as absent; the required check below catches it
            var problem = def.Type switch
            {
                AttributeTypes.Text => value is string ? null : "Must be text.",
                AttributeTypes.Number => value is int or long or double or float or decimal ? null : "Must be a number.",
                AttributeTypes.Bool => value is bool ? null : "Must be true or false.",
                AttributeTypes.Enum => value is string s && def.Options?.Contains(s) == true
                    ? null : $"Must be one of: {string.Join(", ", def.Options ?? [])}.",
                _ => "Unsupported attribute type.",
            };
            if (problem is not null) errors[$"attributes.{key}"] = [problem];
        }

        foreach (var def in schema.Where(d => d.Required))
        {
            if (!values.TryGetValue(def.Key, out var v) || v is null || v is string { Length: 0 })
                errors[$"attributes.{def.Key}"] = ["Required."];
        }

        return errors;
    }
}
