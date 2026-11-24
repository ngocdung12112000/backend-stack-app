namespace BuildingBlocks.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "stackshop-identity";
    public string Audience { get; set; } = "stackshop-api";

    /// <summary>
    /// URL of the identity service's JWKS document (e.g. http://identity:8080/.well-known/jwks.json).
    /// Keys are cached and refreshed when an unknown <c>kid</c> shows up, which makes key rotation work.
    /// </summary>
    public string? JwksUrl { get; set; }

    /// <summary>PEM-encoded RSA public key; alternative to <see cref="JwksUrl"/> (used by the identity service itself).</summary>
    public string? PublicKeyPem { get; set; }
}
