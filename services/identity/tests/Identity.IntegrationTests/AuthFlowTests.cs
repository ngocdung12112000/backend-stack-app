using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Identity.IntegrationTests;

[Collection(IdentityCollection.Name)]
public class AuthFlowTests(IdentityFactory factory)
{
    private IdentityClient NewClient() => new(factory);

    [Fact]
    public async Task F01_AC1_Register_Returns201_QueuesConfirmationEmail_AndUserCanLogin()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();

        var response = await api.Post("/api/v1/auth/register", new { email, password = IdentityClient.Password, fullName = "Ann" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var mail = factory.Emails.LastTo(email);
        mail.Subject.Should().Contain("Confirm");
        mail.TextBody.Should().Contain("/verify-email?userId=");

        var login = await api.LoginRaw(email);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("expiresIn").GetInt32().Should().Be(900);
        body.GetProperty("user").GetProperty("roles")[0].GetString().Should().Be("Customer");
    }

    [Fact]
    public async Task F01_AC1_ConfirmEmail_WithEmailedToken_MarksProfileConfirmed_AndTokenIsSingleUse()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        var userId = await api.Register(email);
        var token = CapturingEmailSender.QueryValue(factory.Emails.LastTo(email), "token");
        var tokens = await api.Login(email);

        (await api.Post("/api/v1/auth/confirm-email", new { userId, token })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var me = await (await api.Get("/api/v1/users/me", tokens.Access)).Content.ReadFromJsonAsync<JsonElement>();
        me.GetProperty("emailConfirmed").GetBoolean().Should().BeTrue();

        var again = await api.Post("/api/v1/auth/confirm-email", new { userId, token });
        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await IdentityClient.Code(again)).Should().Be("IDENTITY_TOKEN_INVALID");
    }

    [Fact]
    public async Task F01_AC2_EmailDifferentCase_Returns409()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);

        var response = await api.Post("/api/v1/auth/register", new { email = email.ToUpperInvariant(), password = IdentityClient.Password, fullName = "Dup" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await IdentityClient.Code(response)).Should().Be("IDENTITY_EMAIL_TAKEN");
    }

    [Fact]
    public async Task F01_BR02_WeakPassword_Returns400_WithWeakPasswordCode_AndProblemDetails()
    {
        var response = await NewClient().Post("/api/v1/auth/register",
            new { email = IdentityClient.NewEmail(), password = "short", fullName = "Weak" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("IDENTITY_WEAK_PASSWORD");
        problem.GetProperty("errors").GetProperty("password").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task F01_AC3_FiveWrongPasswords_ThenCorrectOne_Returns423_UntilFifteenMinutesPass()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);

        for (var i = 0; i < 5; i++)
            (await api.LoginRaw(email, "wrong-pass1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var locked = await api.LoginRaw(email);
        locked.StatusCode.Should().Be((HttpStatusCode)423);
        (await IdentityClient.Code(locked)).Should().Be("IDENTITY_LOCKED_OUT");

        factory.Clock.Offset = TimeSpan.FromMinutes(15).Add(TimeSpan.FromSeconds(5));
        try
        {
            (await api.LoginRaw(email)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            factory.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task F01_BR07_LoginWithUnknownEmail_LooksIdenticalToWrongPassword()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);

        var wrong = await api.LoginRaw(email, "wrong-pass1");
        var unknown = await api.LoginRaw(IdentityClient.NewEmail(), "wrong-pass1");

        unknown.StatusCode.Should().Be(wrong.StatusCode);
        (await IdentityClient.Code(unknown)).Should().Be(await IdentityClient.Code(wrong));
    }

    [Fact]
    public async Task F01_BR03_IpThrottle_Returns429_After20FailedLogins()
    {
        var api = NewClient();

        for (var i = 0; i < 20; i++)
            await api.LoginRaw(IdentityClient.NewEmail(), "wrong-pass1");

        var blocked = await api.LoginRaw(IdentityClient.NewEmail(), "wrong-pass1");
        blocked.StatusCode.Should().Be((HttpStatusCode)429);
        blocked.Headers.Should().ContainKey("Retry-After");
        (await IdentityClient.Code(blocked)).Should().Be("IDENTITY_TOO_MANY_ATTEMPTS");
    }

    [Fact]
    public async Task F01_AC4_ReusingARotatedRefreshToken_Returns401_AndRevokesTheSuccessor()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);
        var r1 = (await api.Login(email)).Refresh;

        var rotated = await api.Post("/api/v1/auth/refresh", new { refreshToken = r1 });
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        var r2 = (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString()!;
        r2.Should().NotBe(r1);

        var reuse = await api.Post("/api/v1/auth/refresh", new { refreshToken = r1 });
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await IdentityClient.Code(reuse)).Should().Be("IDENTITY_REFRESH_REUSED");

        (await api.Post("/api/v1/auth/refresh", new { refreshToken = r2 })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F01_BR06_ConcurrentRefreshWithTheSameToken_OnlyOneWins()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);
        var refresh = (await api.Login(email)).Refresh;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => api.Post("/api/v1/auth/refresh", new { refreshToken = refresh })));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(3);
    }

    [Fact]
    public async Task F01_AC5_AfterLogout_TheOldAccessTokenIsRejected_ByJtiBlacklist()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);
        var tokens = await api.Login(email);
        (await api.Get("/api/v1/users/me", tokens.Access)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await api.Post("/api/v1/auth/logout", new { refreshToken = tokens.Refresh }, tokens.Access)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        (await api.Get("/api/v1/users/me", tokens.Access)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.Post("/api/v1/auth/refresh", new { refreshToken = tokens.Refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F01_US015_LogoutAllDevices_RevokesOtherSessions()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);
        var laptop = await api.Login(email);
        var phone = await api.Login(email);

        (await api.Post("/api/v1/auth/logout", new { allDevices = true }, laptop.Access)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await api.Post("/api/v1/auth/refresh", new { refreshToken = phone.Refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F01_US012_Sessions_AreListed_AndOnlyOwnSessionsCanBeRevoked()
    {
        var api = NewClient();
        var ann = IdentityClient.NewEmail();
        var bob = IdentityClient.NewEmail();
        await api.Register(ann);
        await api.Register(bob);
        var annTokens = await api.Login(ann);
        await api.Login(ann);
        var bobTokens = await api.Login(bob);

        var sessions = await (await api.Get("/api/v1/users/me/sessions", annTokens.Access)).Content.ReadFromJsonAsync<JsonElement>();
        sessions.GetArrayLength().Should().Be(2);
        var familyId = sessions[0].GetProperty("familyId").GetGuid();

        (await api.Delete($"/api/v1/users/me/sessions/{familyId}", bobTokens.Access)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await api.Delete($"/api/v1/users/me/sessions/{familyId}", annTokens.Access)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await (await api.Get("/api/v1/users/me/sessions", annTokens.Access)).Content.ReadFromJsonAsync<JsonElement>();
        after.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task F01_AC7_ForgotPassword_Returns202_ForKnownAndUnknownEmails_WithIdenticalBody()
    {
        var api = NewClient();
        var known = IdentityClient.NewEmail();
        var unknown = IdentityClient.NewEmail();
        await api.Register(known);

        var a = await api.Post("/api/v1/auth/forgot-password", new { email = known });
        var b = await api.Post("/api/v1/auth/forgot-password", new { email = unknown });

        a.StatusCode.Should().Be(HttpStatusCode.Accepted);
        b.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await a.Content.ReadAsStringAsync()).Should().Be(await b.Content.ReadAsStringAsync());
        factory.Emails.AnyTo(unknown).Should().BeFalse();
        factory.Emails.LastTo(known).Subject.Should().Contain("Reset");
    }

    [Fact]
    public async Task F01_US014_ResetPassword_ChangesPassword_AndRevokesAllRefreshTokens()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);
        var old = await api.Login(email);
        await api.Post("/api/v1/auth/forgot-password", new { email });
        var token = CapturingEmailSender.QueryValue(factory.Emails.LastTo(email), "token");

        var reset = await api.Post("/api/v1/auth/reset-password", new { email, token, newPassword = "BrandN3wPass" });

        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.Post("/api/v1/auth/refresh", new { refreshToken = old.Refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.LoginRaw(email)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.LoginRaw(email, "BrandN3wPass")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.Post("/api/v1/auth/reset-password", new { email, token, newPassword = "An0therPass1" })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task F01_US016_ChangePassword_NeedsCurrentPassword()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);
        var tokens = await api.Login(email);

        (await api.Post("/api/v1/auth/change-password", new { currentPassword = "nope-nope1", newPassword = "N3wPassword" }, tokens.Access))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await api.Post("/api/v1/auth/change-password", new { currentPassword = IdentityClient.Password, newPassword = "N3wPassword" }, tokens.Access))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.LoginRaw(email, "N3wPassword")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F01_US012_ProtectedEndpoints_RequireAToken()
    {
        var api = NewClient();

        (await api.Get("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.Post("/api/v1/auth/logout", new { })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await api.Get("/api/v1/users/me", "garbage.token.value")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F01_NFR_Jwks_PublishesTheKeyThatSignedTheAccessToken_WithClaims()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);
        var tokens = await api.Login(email);

        var jwksJson = await (await api.Get("/.well-known/jwks.json")).Content.ReadAsStringAsync();
        var jwks = new JsonWebKeySet(jwksJson);
        var parsed = new JsonWebToken(tokens.Access);

        parsed.Alg.Should().Be("RS256");
        jwks.Keys.Should().Contain(k => k.Kid == parsed.Kid);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(tokens.Access, new TokenValidationParameters
        {
            ValidIssuer = "stackshop-identity",
            ValidAudience = "stackshop-api",
            IssuerSigningKeys = jwks.GetSigningKeys(),
        });
        result.IsValid.Should().BeTrue();
        parsed.GetClaim("email").Value.Should().Be(email);
        parsed.Claims.Where(c => c.Type == "role").Select(c => c.Value).Should().Equal("Customer");
        parsed.Claims.Should().Contain(c => c.Type == "perm" && c.Value == "orders:create");
        parsed.Claims.Should().Contain(c => c.Type == "jti");
        (parsed.ValidTo - parsed.ValidFrom).Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task F01_Outbox_RecordsRegisteredAndResetRequestedEvents_InTheSameTransaction()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        var userId = await api.Register(email);
        await api.Post("/api/v1/auth/forgot-password", new { email });

        using var scope = factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IdentityDb>().Outbox
            .Where(o => o.AggregateId == userId.ToString()).ToListAsync();

        rows.Select(r => r.EventType).Should().BeEquivalentTo("identity.user-registered.v1", "identity.password-reset-requested.v1");
        rows.Should().OnlyContain(r => r.Topic == "identity.user-events" && r.ProcessedAt == null);
        JsonDocument.Parse(rows.Single(r => r.EventType == "identity.user-registered.v1").Payload)
            .RootElement.GetProperty("email").GetString().Should().Be(email);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    public async Task Operational_Endpoints_Respond_WithRealStores(string path)
    {
        (await NewClient().Get(path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F01_NFR_Metrics_ExposeLoginOutcomeCounter()
    {
        var api = NewClient();
        var email = IdentityClient.NewEmail();
        await api.Register(email);
        await api.Login(email);

        var metrics = await (await api.Get("/metrics")).Content.ReadAsStringAsync();

        metrics.Should().Contain("stackshop_auth_logins_total").And.Contain("outcome=\"success\"");
    }
}
