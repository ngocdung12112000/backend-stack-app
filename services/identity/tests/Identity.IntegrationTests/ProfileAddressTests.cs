using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class ProfileAddressTests(IdentityFactory factory)
{
    private const string Base = "/api/v1/addresses";

    private static object Body(string name = "Ann", string phone = "+84901234567") => new
    {
        recipientName = name,
        phone,
        line1 = "1 Main St",
        line2 = "Floor 2",
        ward = "W1",
        district = "D1",
        city = "Hanoi",
        countryCode = "vn",
        postalCode = "100000",
    };

    private async Task<(IdentityClient Api, string Email, Guid UserId, Tokens Tokens)> NewUser()
    {
        var api = new IdentityClient(factory);
        var email = IdentityClient.NewEmail();
        var id = await api.Register(email);
        return (api, email, id, await api.Login(email));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<Guid> Create(IdentityClient api, string bearer, string name = "Ann")
    {
        var r = await api.Post(Base, Body(name), bearer);
        r.StatusCode.Should().Be(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task F02_AC1_FirstAddress_IsDefault_SecondIsNot_AndListHasDefaultFirst()
    {
        var (api, _, _, t) = await NewUser();

        var first = await api.Post(Base, Body("A"), t.Access);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Location!.ToString().Should().StartWith(Base + "/");
        var firstJson = await Json(first);
        firstJson.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        firstJson.GetProperty("countryCode").GetString().Should().Be("VN");
        firstJson.GetProperty("phone").GetString().Should().Be("+84901234567");

        var second = await Json(await api.Post(Base, Body("B"), t.Access));
        second.GetProperty("isDefault").GetBoolean().Should().BeFalse();

        var list = await Json(await api.Get(Base, t.Access));
        list.GetArrayLength().Should().Be(2);
        list[0].GetProperty("recipientName").GetString().Should().Be("A");
        list[0].GetProperty("isDefault").GetBoolean().Should().BeTrue();

        (await api.Get(Base)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F02_AC2_SetDefault_SwitchesInOneStep_AndAliasPathWorks()
    {
        var (api, _, _, t) = await NewUser();
        var a = await Create(api, t.Access, "A");
        var b = await Create(api, t.Access, "B");

        var set = await api.Post($"{Base}/{b}/default", null, t.Access);
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(set)).GetProperty("isDefault").GetBoolean().Should().BeTrue();

        var list = await Json(await api.Get("/api/v1/users/me/addresses", t.Access)); // alias
        list.EnumerateArray().Count(x => x.GetProperty("isDefault").GetBoolean()).Should().Be(1);
        list[0].GetProperty("id").GetGuid().Should().Be(b);
        list[1].GetProperty("id").GetGuid().Should().Be(a);
    }

    [Fact]
    public async Task F02_AC3_EleventhAddress_Returns409_AddressLimitReached()
    {
        var (api, _, _, t) = await NewUser();
        for (var i = 0; i < 10; i++) await Create(api, t.Access, $"A{i}");

        var response = await api.Post(Base, Body("11th"), t.Access);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await IdentityClient.Code(response)).Should().Be("IDENTITY_ADDRESS_LIMIT_REACHED");
    }

    [Fact]
    public async Task F02_AC4_OtherUsersAddress_Returns404_ForReplaceDeleteAndSetDefault_AndIsNeverListed()
    {
        var (api, _, _, ann) = await NewUser();
        var (_, _, _, bob) = await NewUser();
        var annAddress = await Create(api, ann.Access, "Ann");

        foreach (var response in new[]
                 {
                     await api.Put($"{Base}/{annAddress}", Body("Hacked"), bob.Access),
                     await api.Delete($"{Base}/{annAddress}", bob.Access),
                     await api.Post($"{Base}/{annAddress}/default", null, bob.Access),
                     await api.Delete($"{Base}/{Guid.NewGuid()}", bob.Access),
                 })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await IdentityClient.Code(response)).Should().Be("IDENTITY_ADDRESS_NOT_FOUND");
        }

        (await Json(await api.Get(Base, bob.Access))).GetArrayLength().Should().Be(0);
        var mine = await Json(await api.Get(Base, ann.Access));
        mine.GetArrayLength().Should().Be(1);
        mine[0].GetProperty("recipientName").GetString().Should().Be("Ann");
    }

    [Fact]
    public async Task F02_BR02_DeletingTheDefault_PromotesTheMostRecentlyUpdated()
    {
        var (api, _, _, t) = await NewUser();
        var a = await Create(api, t.Access, "A");
        var b = await Create(api, t.Access, "B");
        var c = await Create(api, t.Access, "C");
        (await api.Put($"{Base}/{b}", Body("B2"), t.Access)).StatusCode.Should().Be(HttpStatusCode.OK); // b updated last

        (await api.Delete($"{Base}/{a}", t.Access)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Json(await api.Get(Base, t.Access));
        list.GetArrayLength().Should().Be(2);
        list[0].GetProperty("id").GetGuid().Should().Be(b);
        list[0].GetProperty("isDefault").GetBoolean().Should().BeTrue();
        list[1].GetProperty("id").GetGuid().Should().Be(c);
        list[1].GetProperty("isDefault").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task F02_BR03_BR04_InvalidPhoneAndMissingFields_Return400_WithFieldErrors()
    {
        var (api, _, _, t) = await NewUser();

        var badPhone = await api.Post(Base, Body(phone: "0901234567"), t.Access);
        badPhone.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(badPhone)).GetProperty("errors").TryGetProperty("phone", out _).Should().BeTrue();

        var empty = await api.Post(Base, new { recipientName = "", phone = "", line1 = "", city = "", countryCode = "VNM" }, t.Access);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await Json(empty)).GetProperty("errors");
        foreach (var field in new[] { "recipientName", "phone", "line1", "city", "countryCode" })
            errors.TryGetProperty(field, out _).Should().BeTrue(field);
        (await Json(await api.Get(Base, t.Access))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task F02_AC2_AC3_ConcurrentWriters_KeepExactlyOneDefault_AndAtMostTenAddresses()
    {
        var (api, _, userId, t) = await NewUser();

        var results = await Task.WhenAll(Enumerable.Range(0, 14).Select(i => api.Post(Base, Body($"A{i}"), t.Access)));
        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(10);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(4);

        var ids = (await Json(await api.Get(Base, t.Access))).EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        await Task.WhenAll(ids.Select(id => api.Post($"{Base}/{id}/default", null, t.Access)));
        await Task.WhenAll(ids.Take(5).Select(id => api.Delete($"{Base}/{id}", t.Access)));

        using var scope = factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IdentityDb>().Addresses.AsNoTracking().Where(a => a.UserId == userId).ToListAsync();
        rows.Count(a => a.IsDefault).Should().Be(1);
        rows.Should().HaveCountLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task F02_US021_Patch_UpdatesNamePhoneAndAvatar_AndGetMeStaysBackwardCompatible()
    {
        var (api, email, _, t) = await NewUser();
        var avatar = Guid.NewGuid();

        var patch = await api.Patch("/api/v1/users/me", new { fullName = "New Name", phone = "+84 901 234 567", avatarAssetId = avatar }, t.Access);

        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());
        var me = await Json(await api.Get("/api/v1/users/me", t.Access));
        me.GetProperty("email").GetString().Should().Be(email);
        me.GetProperty("fullName").GetString().Should().Be("New Name");
        me.GetProperty("phone").GetString().Should().Be("+84901234567");
        me.GetProperty("avatarAssetId").GetGuid().Should().Be(avatar);
        foreach (var legacy in new[] { "id", "emailConfirmed", "status", "roles", "permissions", "createdAt" })
            me.TryGetProperty(legacy, out _).Should().BeTrue(legacy);

        var onlyName = await Json(await api.Patch("/api/v1/users/me", new { fullName = "Other" }, t.Access));
        onlyName.GetProperty("phone").GetString().Should().Be("+84901234567");

        var cleared = await Json(await api.Patch("/api/v1/users/me", new { phone = "", avatarAssetId = Guid.Empty }, t.Access));
        cleared.GetProperty("phone").ValueKind.Should().Be(JsonValueKind.Null);
        cleared.GetProperty("avatarAssetId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task F02_US021_Patch_InvalidInput_Returns400()
    {
        var (api, _, _, t) = await NewUser();

        var badPhone = await api.Patch("/api/v1/users/me", new { phone = "12345" }, t.Access);
        badPhone.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(badPhone)).GetProperty("errors").TryGetProperty("phone", out _).Should().BeTrue();
        (await api.Patch("/api/v1/users/me", new { fullName = "" }, t.Access)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await api.Patch("/api/v1/users/me", new { }, t.Access)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await api.Patch("/api/v1/users/me", new { fullName = "X" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F02_AC5_BR06_DeleteAccount_WrongPassword_Returns400_AndChangesNothing()
    {
        var (api, email, _, t) = await NewUser();
        await Create(api, t.Access);

        var response = await api.DeleteWithBody("/api/v1/users/me", new { password = "Wrong-pass1" }, t.Access);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await IdentityClient.Code(response)).Should().Be("IDENTITY_INVALID_CURRENT_PASSWORD");
        (await api.LoginRaw(email)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await api.Get(Base, t.Access))).GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task F02_AC5_BR06_DeleteAccount_Returns202_Anonymises_RevokesTokens_BlocksLogin_AndWritesOutbox()
    {
        var (api, email, userId, t) = await NewUser();
        await api.Patch("/api/v1/users/me", new { phone = "+84901234567" }, t.Access);
        await Create(api, t.Access);
        var second = await api.Login(email); // another session

        var response = await api.DeleteWithBody("/api/v1/users/me", new { password = IdentityClient.Password }, t.Access);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await api.LoginRaw(email)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.Post("/api/v1/auth/refresh", new { refreshToken = t.Refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.Post("/api/v1/auth/refresh", new { refreshToken = second.Refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.Get("/api/v1/users/me", t.Access)).StatusCode.Should().Be(HttpStatusCode.Unauthorized); // jti blacklisted
        (await api.Get("/api/v1/users/me", second.Access)).StatusCode.Should().Be(HttpStatusCode.NotFound);  // other device: user is gone
        (await api.Post(Base, Body(), second.Access)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDb>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        user.Status.ToString().Should().Be("Deleted");
        user.Email.Should().Be($"deleted+{userId}@invalid");
        user.FullName.Should().BeNull();
        user.Phone.Should().BeNull();
        (await db.Addresses.CountAsync(a => a.UserId == userId)).Should().Be(0);
        (await db.RefreshTokens.Where(x => x.UserId == userId).ToListAsync()).Should().OnlyContain(x => x.RevokedAt != null);
        var events = await db.Outbox.Where(m => m.EventType == "identity.user-deleted.v1" && m.AggregateId == userId.ToString()).ToListAsync();
        events.Should().ContainSingle().Which.Payload.Should().NotContain(email);
        events[0].Topic.Should().Be("identity.user-events");

        // The email is free again for a fresh registration.
        (await api.Post("/api/v1/auth/register", new { email, password = IdentityClient.Password, fullName = "Again" })).StatusCode
            .Should().Be(HttpStatusCode.Created);
    }
}
