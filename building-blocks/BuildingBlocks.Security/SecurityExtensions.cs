using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace BuildingBlocks.Security;

public static class SecurityExtensions
{
    /// <summary>
    /// Validates RS256 JWTs. Until a public key is configured (before the identity service exists),
    /// authentication is registered but no token validates.
    /// </summary>
    public static IServiceCollection AddJwtValidation(this IServiceCollection services, IConfiguration config)
    {
        var opts = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                var parameters = new TokenValidationParameters
                {
                    ValidIssuer = opts.Issuer,
                    ValidAudience = opts.Audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                if (!string.IsNullOrWhiteSpace(opts.PublicKeyPem))
                {
                    var rsa = RSA.Create();
                    rsa.ImportFromPem(opts.PublicKeyPem);
                    parameters.IssuerSigningKey = new RsaSecurityKey(rsa);
                }
                o.TokenValidationParameters = parameters;
            });
        services.AddAuthorization();
        return services;
    }
}
