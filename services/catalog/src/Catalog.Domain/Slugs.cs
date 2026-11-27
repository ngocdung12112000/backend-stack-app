using System.Text;
using System.Text.RegularExpressions;

namespace Catalog.Domain;

/// <summary>BR-01: slugs are lowercase, ASCII-folded and hyphenated. Collisions get a numeric suffix starting at <c>-2</c>.</summary>
public static class Slugs
{
    public const int MaxLength = 120;

    // Own folding table instead of string.Normalize: the runtime images use InvariantGlobalization (no ICU),
    // where normalization is not reliable for non-ASCII text.
    private static readonly Dictionary<char, string> Fold = BuildFoldTable();

    private static readonly Regex ValidPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static bool IsValid(string? slug) => !string.IsNullOrEmpty(slug) && slug.Length <= MaxLength && ValidPattern.IsMatch(slug);

    public static string From(string name)
    {
        var sb = new StringBuilder(name.Length);
        var pendingDash = false;
        foreach (var raw in name.Trim().ToLowerInvariant())
        {
            var piece = raw is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? raw.ToString()
                : Fold.TryGetValue(raw, out var folded) ? folded : null;
            if (piece is null) { pendingDash = sb.Length > 0; continue; }
            if (pendingDash) sb.Append('-');
            pendingDash = false;
            sb.Append(piece);
        }

        var slug = sb.ToString();
        if (slug.Length > MaxLength) slug = slug[..MaxLength].TrimEnd('-');
        return slug.Length == 0 ? "item" : slug;
    }

    /// <summary>Returns <paramref name="baseSlug"/>, or the first of <c>base-2</c>, <c>base-3</c>... that is not <paramref name="isTaken"/>.</summary>
    public static string NextAvailable(string baseSlug, Func<string, bool> isTaken)
    {
        if (!isTaken(baseSlug)) return baseSlug;
        for (var n = 2; ; n++)
        {
            var suffix = "-" + n;
            var stem = baseSlug.Length + suffix.Length > MaxLength ? baseSlug[..(MaxLength - suffix.Length)].TrimEnd('-') : baseSlug;
            var candidate = stem + suffix;
            if (!isTaken(candidate)) return candidate;
        }
    }

    private static Dictionary<char, string> BuildFoldTable()
    {
        var map = new Dictionary<char, string>();
        void Add(string target, string sources) { foreach (var c in sources) map[c] = target; }
        Add("a", "àáâãäåāăąǎạảấầẩẫậắằẳẵặ");
        Add("c", "çćĉċč");
        Add("d", "ďđ");
        Add("e", "èéêëēĕėęěẹẻẽếềểễệ");
        Add("g", "ĝğġģ");
        Add("h", "ĥħ");
        Add("i", "ìíîïĩīĭįıǐỉị");
        Add("j", "ĵ");
        Add("k", "ķ");
        Add("l", "ĺļľŀł");
        Add("n", "ñńņňŉ");
        Add("o", "òóôõöøōŏőǒọỏốồổỗộớờởỡợơ");
        Add("r", "ŕŗř");
        Add("s", "śŝşš");
        Add("t", "ţťŧ");
        Add("u", "ùúûüũūŭůűųǔụủứừửữựư");
        Add("w", "ŵ");
        Add("y", "ýÿŷỳỵỷỹ");
        Add("z", "źżž");
        map['ß'] = "ss"; map['æ'] = "ae"; map['œ'] = "oe"; map['þ'] = "th";
        return map;
    }
}
