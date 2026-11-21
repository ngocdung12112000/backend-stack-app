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
}
