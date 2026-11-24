using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class AdminTests(IdentityFactory factory)
{
    private IdentityClient NewClient() => new(factory);

    private async Task<(IdentityClient Api, Tokens Admin)> AdminSession()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await factory.CreateAdminAsync(email, IdentityClient.Password);
        return (api, await api.Login(email));
    }

    [Fact]
    public async Task F01_US017_CustomerToken_CannotUseAdminEndpoints_Returns403()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);
        var customer = await api.Login(email);

        (await api.Get("/api/v1/admin/users", customer.Access)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await api.Post($"/api/v1/admin/users/{Guid.NewGuid()}/lock", null, customer.Access)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await api.Get("/api/v1/admin/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F01_US017_Admin_ListsUsers_WithSearchAndPaging()
    {
        var (api, admin) = await AdminSession();
        var email = IdentityClient.NewEmail();
        await api.Register(email);

        var page = await (await api.Get($"/api/v1/admin/users?search={email}&page=1&pageSize=5", admin.Access)).Content.ReadFromJsonAsync<JsonElement>();

        page.GetProperty("totalCount").GetInt64().Should().Be(1);
        page.GetProperty("items")[0].GetProperty("email").GetString().Should().Be(email);

        var admins = await (await api.Get("/api/v1/admin/users?role=Admin&pageSize=100", admin.Access)).Content.ReadFromJsonAsync<JsonElement>();
        admins.GetProperty("items").EnumerateArray().Should().OnlyContain(u => u.GetProperty("roles").EnumerateArray().Any(r => r.GetString() == "Admin"));
    }

    [Fact]
    public async Task F01_US017_Admin_AssignsSupportRole_AndNextLoginCarriesThePermissions()
    {
        var (api, admin) = await AdminSession();
        var email = IdentityClient.NewEmail();
        var userId = await api.Register(email);

        (await api.Put($"/api/v1/admin/users/{userId}/roles", new { roles = new[] { "Customer", "Support" } }, admin.Access))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var support = await api.Login(email);
        (await api.Get("/api/v1/admin/users", support.Access)).StatusCode.Should().Be(HttpStatusCode.OK);          // users:read
        (await api.Post($"/api/v1/admin/users/{userId}/unlock", null, support.Access)).StatusCode.Should().Be(HttpStatusCode.Forbidden); // no users:write

        var bad = await api.Put($"/api/v1/admin/users/{userId}/roles", new { roles = new[] { "Wizard" } }, admin.Access);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await IdentityClient.Code(bad)).Should().Be("IDENTITY_UNKNOWN_ROLE");
    }

    [Fact]
    public async Task F01_US018_Admin_LocksUser_LoginAndRefreshFail_ThenUnlockRestoresAccess()
    {
        var (api, admin) = await AdminSession();
        var email = IdentityClient.NewEmail();
        var userId = await api.Register(email);
        var session = await api.Login(email);

        (await api.Post($"/api/v1/admin/users/{userId}/lock", null, admin.Access)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await api.LoginRaw(email)).StatusCode.Should().Be((HttpStatusCode)423);
        (await api.Post("/api/v1/auth/refresh", new { refreshToken = session.Refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using (var scope = factory.Services.CreateScope())
        {
            var events = await scope.ServiceProvider.GetRequiredService<IdentityDb>().Outbox
                .Where(o => o.AggregateId == userId.ToString() && o.EventType == "identity.user-locked.v1").ToListAsync();
            events.Should().ContainSingle();
        }

        (await api.Post($"/api/v1/admin/users/{userId}/unlock", null, admin.Access)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.LoginRaw(email)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F01_US018_Admin_CannotLockSelf()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        var id = await factory.CreateAdminAsync(email, IdentityClient.Password);
        var admin = await api.Login(email);

        var response = await api.Post($"/api/v1/admin/users/{id}/lock", null, admin.Access);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await IdentityClient.Code(response)).Should().Be("IDENTITY_CANNOT_LOCK_SELF");
    }

    [Fact]
    public async Task F01_US018_LockUnknownUser_Returns404()
    {
        var (api, admin) = await AdminSession();

        (await api.Post($"/api/v1/admin/users/{Guid.NewGuid()}/lock", null, admin.Access)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
