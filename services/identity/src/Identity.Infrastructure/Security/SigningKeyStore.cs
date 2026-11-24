using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Identity.Infrastructure.Security;

/// <summary>
/// Where the RS256 signing key comes from. Production: <see cref="PrivateKeyPem"/> from a Secret (env var) or
/// <see cref="PrivateKeyPath"/> from a mounted Secret file. Development only: <see cref="GenerateIfMissing"/>
/// creates a key (and persists it to <see cref="PrivateKeyPath"/> when that path is writable) so restarts keep issued tokens valid.
/// </summary>
public sealed class SigningOptions
{
    public const string Section = "Signing";

    public string? PrivateKeyPem { get; set; }
    public string? PrivateKeyPath { get; set; }
    public bool GenerateIfMissing { get; set; }

    /// <summary>Optional explicit key id; defaults to a thumbprint of the public key.</summary>
    public string? KeyId { get; set; }

    /// <summary>Public keys of previous signing keys, still published in the JWKS so tokens signed before a rotation stay valid.</summary>
    public string[] RetiredPublicKeyPems { get; set; } = [];
}

public sealed class SigningKeyStore : IDisposable
{
    private readonly RSA _rsa;
    private readonly List<Dictionary<string, string>> _jwks = [];

    public SigningKeyStore(IOptions<SigningOptions> options, ILogger<SigningKeyStore> logger)
    {
        var o = options.Value;
        _rsa = Load(o, logger);

        KeyId = !string.IsNullOrWhiteSpace(o.KeyId) ? o.KeyId : Thumbprint(_rsa);
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        PublicKeyPem = _rsa.ExportSubjectPublicKeyInfoPem();

        _jwks.Add(ToJwk(_rsa, KeyId));
        foreach (var pem in o.RetiredPublicKeyPems.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var retired = RSA.Create();
            retired.ImportFromPem(pem);
            _jwks.Add(ToJwk(retired, Thumbprint(retired)));
        }
    }

    public string KeyId { get; }
    public RsaSecurityKey SigningKey { get; }
    public string PublicKeyPem { get; }

    /// <summary>The JWKS document (public keys only).</summary>
    public object Jwks => new { keys = _jwks };

    private static RSA Load(SigningOptions o, ILogger logger)
    {
        var rsa = RSA.Create();
        if (!string.IsNullOrWhiteSpace(o.PrivateKeyPem))
        {
            rsa.ImportFromPem(o.PrivateKeyPem);
            return rsa;
        }

        if (!string.IsNullOrWhiteSpace(o.PrivateKeyPath) && File.Exists(o.PrivateKeyPath))
        {
            rsa.ImportFromPem(File.ReadAllText(o.PrivateKeyPath));
            return rsa;
        }

        if (!o.GenerateIfMissing)
            throw new InvalidOperationException(
                "No signing key configured. Set Signing:PrivateKeyPem or Signing:PrivateKeyPath (Secret), or Signing:GenerateIfMissing=true for local development only.");

        rsa = RSA.Create(2048);
        if (!string.IsNullOrWhiteSpace(o.PrivateKeyPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.PrivateKeyPath))!);
                File.WriteAllText(o.PrivateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
                logger.LogWarning("DEV ONLY: generated a new RSA signing key at {Path}. Never use GenerateIfMissing in production.", o.PrivateKeyPath);
                return rsa;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not persist the generated signing key to {Path}", o.PrivateKeyPath);
            }
        }

        logger.LogWarning("DEV ONLY: using an ephemeral in-memory RSA signing key; tokens become invalid when the service restarts.");
        return rsa;
    }

    private static string Thumbprint(RSA rsa) =>
        Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..22];

    private static Dictionary<string, string> ToJwk(RSA rsa, string kid)
    {
        var p = rsa.ExportParameters(false);
        return new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = SecurityAlgorithms.RsaSha256,
            ["kid"] = kid,
            ["n"] = Base64UrlEncoder.Encode(p.Modulus!),
            ["e"] = Base64UrlEncoder.Encode(p.Exponent!),
        };
    }

    public void Dispose() => _rsa.Dispose();
}
