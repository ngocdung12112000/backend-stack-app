namespace BuildingBlocks.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "stackshop-identity";
    public string Audience { get; set; } = "stackshop";

    /// <summary>PEM-encoded RSA public key used to validate RS256 tokens issued by the identity service.</summary>
    public string? PublicKeyPem { get; set; }
}
