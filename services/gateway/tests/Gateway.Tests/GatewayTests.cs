using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Tests;

public class GatewayTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    public async Task OperationalEndpoints_Respond(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Config_RoutesProducts_ToCatalogCluster()
    {
        var config = factory.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();

        var route = Assert.Single(config.Routes, r => r.RouteId == "catalog-products");
        Assert.Equal("catalog", route.ClusterId);
        Assert.Contains(config.Clusters, c => c.ClusterId == "catalog");
    }

    [Fact]
    public async Task SwaggerUi_IsServed()
    {
        var response = await factory.CreateClient().GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Config_ProxiesCatalogSwaggerJson_ToCatalogCluster()
    {
        var config = factory.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();

        var route = Assert.Single(config.Routes, r => r.RouteId == "docs-catalog");
        Assert.Equal("catalog", route.ClusterId);
        Assert.Equal("/docs/catalog/swagger.json", route.Match.Path);
    }

    [Fact]
    public void Config_StripsInboundUserHeaders()
    {
        var config = factory.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();
        var route = config.Routes.Single(r => r.RouteId == "catalog-products");

        var removed = route.Transforms!
            .Where(t => t.ContainsKey("RequestHeaderRemove"))
            .Select(t => t["RequestHeaderRemove"])
            .ToList();

        Assert.Contains("X-User-Id", removed);
    }

    [Theory]
    [InlineData("catalog-categories", "/api/v1/categories/{**catch-all}", "anonymous")]
    [InlineData("catalog-brands", "/api/v1/brands/{**catch-all}", "anonymous")]
    [InlineData("catalog-admin-products", "/api/v1/admin/products/{**catch-all}", "default")]
    [InlineData("catalog-admin-categories", "/api/v1/admin/categories/{**catch-all}", "default")]
    [InlineData("catalog-admin-brands", "/api/v1/admin/brands/{**catch-all}", "default")]
    public void F03_Config_RoutesCatalogPaths_AndRequiresAuthenticationForAdminOnes(string routeId, string path, string policy)
    {
        var config = factory.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();

        var route = Assert.Single(config.Routes, r => r.RouteId == routeId);
        Assert.Equal("catalog", route.ClusterId);
        Assert.Equal(path, route.Match.Path);
        Assert.Equal(policy, route.AuthorizationPolicy);
        Assert.Contains(route.Transforms!, t => t.TryGetValue("RequestHeaderRemove", out var h) && h == "X-User-Permissions");
    }

    [Fact]
    public async Task F03_Gateway_AdminCatalogRoute_WithoutToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/admin/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
