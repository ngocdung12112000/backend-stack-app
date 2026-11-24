using FluentValidation;
using Identity.Application;
using Identity.Application.Auth;
using Identity.Application.Users;
using Identity.Domain;
using MediatR;

namespace Identity.Tests;

public class SessionTests
{
    [Fact]
    public async Task F01_US012_ListSessions_ReturnsOneEntryPerLiveFamily()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        await h.LoggedIn();
        var second = await h.LoggedIn();
        await h.Refresh.Handle(new RefreshCommand(second.RefreshToken, h.Client), default);

        var result = await new ListSessionsHandler(h.Store, h.Time).Handle(new ListSessionsQuery(user.Id), default);

        result.Value.Should().HaveCount(2);
        result.Value.Select(s => s.FamilyId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task F01_US012_RevokeSession_OtherUsersFamily_Returns404()
    {
        var h = new Harness();
        var ann = await h.RegisteredUser("ann@example.com");
        var bob = await h.RegisteredUser("bob@example.com");
        await h.LoggedIn("ann@example.com");
        var family = h.Store.RefreshTokens.Single().FamilyId;
        var handler = new RevokeSessionHandler(h.Store, h.Store, h.Time);

        (await handler.Handle(new RevokeSessionCommand(bob.Id, family), default)).Error.Code.Should().Be("IDENTITY_SESSION_NOT_FOUND");
        (await handler.Handle(new RevokeSessionCommand(ann.Id, family), default)).IsSuccess.Should().BeTrue();
        h.Store.RefreshTokens.Should().OnlyContain(t => t.IsRevoked);
    }
}

public class ProfileAndAdminTests
{
    [Fact]
    public async Task F01_US012_GetMe_ReturnsProfileWithRolesAndPermissions()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();

        var result = await new GetMeHandler(h.Store).Handle(new GetMeQuery(user.Id), default);

        result.Value.Email.Should().Be("ann@example.com");
        result.Value.Roles.Should().Equal(RoleNames.Customer);
        result.Value.EmailConfirmed.Should().BeFalse();
        (await new GetMeHandler(h.Store).Handle(new GetMeQuery(Guid.NewGuid()), default)).Error.Code.Should().Be("IDENTITY_USER_NOT_FOUND");
    }

    [Fact]
    public async Task F01_US013_ResendConfirmation_InvalidatesOldLink_AndSendsNewOne()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        var handler = new ResendConfirmationHandler(h.Store, h.Store, h.Store, h.Emails, h.Options, h.Time);

        (await handler.Handle(new ResendConfirmationCommand(user.Id), default)).IsSuccess.Should().BeTrue();

        h.Email.Sent.Should().HaveCount(2);
        h.Store.EmailTokens.Count(t => t.IsUsable(h.Time.GetUtcNow().UtcDateTime)).Should().Be(1);

        user.ConfirmEmail(h.Time.GetUtcNow().UtcDateTime);
        await handler.Handle(new ResendConfirmationCommand(user.Id), default);
        h.Email.Sent.Should().HaveCount(2); // already confirmed: nothing sent
    }

    [Fact]
    public async Task F01_US018_Unlock_RestoresAccess()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        user.Lock(h.Time.GetUtcNow().UtcDateTime);

        (await new UnlockUserHandler(h.Store, h.Store, h.Time).Handle(new UnlockUserCommand(user.Id), default)).IsSuccess.Should().BeTrue();

        (await h.Login.Handle(new LoginCommand("ann@example.com", Harness.Password, h.Client), default)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task F01_BR08_CreateAdmin_AssignsAdminRole_AndConfirmsEmail()
    {
        var h = new Harness();
        var handler = new CreateAdminHandler(h.Store, h.Store, h.Hasher, h.Store, h.Time);

        var result = await handler.Handle(new CreateAdminCommand("root@example.com", Harness.Password, "Root"), default);

        var admin = h.Store.Users.Single(u => u.Id == result.Value);
        admin.RoleNames.Should().BeEquivalentTo(RoleNames.Admin, RoleNames.Customer);
        admin.EmailConfirmed.Should().BeTrue();
        (await handler.Handle(new CreateAdminCommand("ROOT@example.com", Harness.Password, "Root"), default)).Error.Code.Should().Be("IDENTITY_EMAIL_TAKEN");
    }

    [Fact]
    public async Task F01_US017_ListUsers_ParsesStatusFilter()
    {
        var h = new Harness();
        await h.RegisteredUser();
        var handler = new ListUsersHandler(h.Store);

        var result = await handler.Handle(new ListUsersQuery(null, null, "active", 1, 20), default);

        result.Value.Items.Should().ContainSingle();
        new ListUsersValidator().Validate(new ListUsersQuery(null, null, "bogus", 1, 20)).IsValid.Should().BeFalse();
        new ListUsersValidator().Validate(new ListUsersQuery(null, null, null, 0, 500)).Errors.Should().HaveCount(2);
    }
}

public class ValidationAndTokenTests
{
    [Fact]
    public async Task ValidationBehavior_ThrowsWithAllFailures_AndSkipsTheHandler()
    {
        var behavior = new ValidationBehavior<RegisterCommand, string>([new RegisterValidator()]);
        var called = false;

        var act = () => behavior.Handle(new RegisterCommand("not-an-email", "x", ""), () => { called = true; return Task.FromResult("ok"); }, default);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Select(e => e.PropertyName).Should().Contain(["Email", "FullName", "Password"]);
        called.Should().BeFalse();
    }

    [Fact]
    public async Task ValidationBehavior_PassesValidRequestsThrough()
    {
        var behavior = new ValidationBehavior<RegisterCommand, string>([new RegisterValidator()]);

        (await behavior.Handle(new RegisterCommand("a@b.co", "abcdefg1", "A"), () => Task.FromResult("ok"), default)).Should().Be("ok");
    }

    [Fact]
    public void SecureToken_IsRandom_AndHashIsStableHex64()
    {
        var a = SecureToken.Generate();
        var b = SecureToken.Generate();

        a.Should().NotBe(b).And.MatchRegex("^[A-Za-z0-9_-]{43}$");
        SecureToken.Hash(a).Should().HaveLength(64).And.Be(SecureToken.Hash(a));
    }

    [Fact]
    public void AuthOptions_DefaultsMatchTheSpec()
    {
        var o = new AuthOptions();

        o.AccessTokenLifetime.Should().Be(TimeSpan.FromMinutes(15)); // BR-05
        o.MaxFailedLogins.Should().Be(5);                            // BR-03
        o.Lockout.Should().Be(TimeSpan.FromMinutes(15));
        o.IpAttemptLimit.Should().Be(20);
        o.ResetTokenMinutes.Should().Be(30);
        o.ConfirmTokenHours.Should().Be(24);
    }
}
