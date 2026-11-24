using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BuildingBlocks.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Tests;

/// <summary>Gateway side of F01: identity routes, RS256 validation and the jti blacklist.</summary>
public class IdentityRoutingTests : IClassFixture<IdentityRoutingTests.GatewayWithKey>
{
    private readonly GatewayWithKey _factory;

    public IdentityRoutingTests(GatewayWithKey factory) => _factory = factory;

    public sealed class FakeBlacklist : IJtiBlacklist
    {
        public HashSet<string> Revoked { get; } = [];
        public Task RevokeAsync(string jti, TimeSpan ttl, CancellationToken ct = default) { Revoked.Add(jti); return Task.CompletedTask; }
        public Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default) => Task.FromResult(Revoked.Contains(jti));
    }

    public sealed class GatewayWithKey : WebApplicationFactory<Program>
    {
        public RSA Key { get; } = RSA.Create(2048);
        public FakeBlacklist Blacklist { get; } = new();

        public GatewayWithKey()
        {
            // Env vars are visible to the eager configuration read in AddJwtValidation.
            Environment.SetEnvironmentVariable("Jwt__PublicKeyPem", Key.ExportSubjectPublicKeyInfoPem());
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(s =>
            {
                s.RemoveAll<IJtiBlacklist>();
                s.AddSingleton<IJtiBlacklist>(Blacklist);
                // Unreachable on purpose: a request that passes authentication ends in 502 instead of reaching a service.
                s.AddSingleton<IProxyConfigProvider>(new TestConfig());
            });
    }

    private sealed class TestConfig : IProxyConfigProvider
    {
        public IProxyConfig GetConfig() => new Cfg();

        private sealed class Cfg : IProxyConfig
        {
            public IReadOnlyList<RouteConfig> Routes { get; } =
            [
                new RouteConfig
                {
                    RouteId = "test-users", ClusterId = "test-upstream", AuthorizationPolicy = "default",
                    Match = new RouteMatch { Path = "/test/protected/{**catch-all}" },
                },
            ];
            public IReadOnlyList<ClusterConfig> Clusters { get; } =
            [
                new ClusterConfig
                {
                    ClusterId = "test-upstream",
                    Destinations = new Dictionary<string, DestinationConfig> { ["d"] = new() { Address = "http://127.0.0.1:1/" } },
                },
            ];
            public Microsoft.Extensions.Primitives.IChangeToken ChangeToken { get; } = new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
        }
    }

    private string Token(string jti, TimeSpan? lifetime = null, string audience = "stackshop-api")
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "stackshop-identity",
            Audience = audience,
            NotBefore = now.AddMinutes(-1),
            Expires = now + (lifetime ?? TimeSpan.FromMinutes(15)),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_factory.Key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["jti"] = jti },
        });
    }

    private async Task<HttpStatusCode> GetUsersMe(string? token)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/test/protected/me");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task F01_Gateway_ProtectedRoute_WithoutToken_Returns401()
    {
        (await GetUsersMe(null)).Should().NotBe(HttpStatusCode.OK).And.Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F01_Gateway_ProtectedRoute_WithValidToken_PassesAuthentication()
    {
        var status = await GetUsersMe(Token(Guid.NewGuid().ToString()));

        status.Should().NotBe(HttpStatusCode.Unauthorized); // reaches the (unreachable) upstream => 502
    }

    [Fact]
    public async Task F01_AC5_Gateway_RejectsTokenWhoseJtiIsBlacklisted()
    {
        var jti = Guid.NewGuid().ToString();
        var token = Token(jti);
        (await GetUsersMe(token)).Should().NotBe(HttpStatusCode.Unauthorized);

        _factory.Blacklist.Revoked.Add(jti);

        (await GetUsersMe(token)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F01_Gateway_RejectsTokenForAnotherAudience_AndTamperedSignature()
    {
        (await GetUsersMe(Token("j1", audience: "someone-else"))).Should().Be(HttpStatusCode.Unauthorized);

        var valid = Token("j2");
        var tampered = valid[..^4] + (valid.EndsWith("AAAA") ? "BBBB" : "AAAA");
        (await GetUsersMe(tampered)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F01_Gateway_RejectsTokenSignedWithAnUnknownKey()
    {
        using var other = RSA.Create(2048);
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "stackshop-identity",
            Audience = "stackshop-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(other), SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["sub"] = "x", ["jti"] = "j3" },
        });

        (await GetUsersMe(forged)).Should().Be(HttpStatusCode.Unauthorized);
    }
}

public class IdentityRouteConfigTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private IProxyConfig Config => factory.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();

    [Theory]
    [InlineData("identity-auth", "/api/v1/auth/{**catch-all}", "anonymous")]
    [InlineData("identity-jwks", "/.well-known/jwks.json", "anonymous")]
    [InlineData("identity-users", "/api/v1/users/{**catch-all}", "default")]
    [InlineData("identity-admin-users", "/api/v1/admin/users/{**catch-all}", "default")]
    [InlineData("docs-identity", "/docs/identity/swagger.json", "anonymous")]
    public void F01_Config_RoutesIdentityPaths_ToIdentityCluster(string routeId, string path, string policy)
    {
        var route = Assert.Single(Config.Routes, r => r.RouteId == routeId);

        route.ClusterId.Should().Be("identity");
        route.Match.Path.Should().Be(path);
        route.AuthorizationPolicy.Should().Be(policy);
        Config.Clusters.Should().Contain(c => c.ClusterId == "identity");
    }

    [Theory]
    [InlineData("identity-auth")]
    [InlineData("identity-users")]
    [InlineData("identity-admin-users")]
    public void F01_Config_IdentityRoutes_StripInboundUserHeaders(string routeId)
    {
        var removed = Config.Routes.Single(r => r.RouteId == routeId).Transforms!
            .Where(t => t.ContainsKey("RequestHeaderRemove")).Select(t => t["RequestHeaderRemove"]).ToList();

        removed.Should().Contain(["X-User-Id", "X-User-Roles", "X-User-Permissions"]);
    }
}
