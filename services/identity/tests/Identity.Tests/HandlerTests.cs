using FluentValidation;
using Identity.Application;
using Identity.Application.Auth;
using Identity.Application.Users;
using Identity.Domain;

namespace Identity.Tests;

public class RegisterTests
{
    [Fact]
    public async Task F01_AC1_Register_CreatesCustomer_QueuesConfirmationEmail_AndWritesOutbox()
    {
        var h = new Harness();

        var user = await h.RegisteredUser();

        user.RoleNames.Should().Equal(RoleNames.Customer);
        user.EmailConfirmed.Should().BeFalse();
        h.Email.Sent.Should().ContainSingle().Which.Subject.Should().Contain("Confirm");
        h.Email.Sent[0].To.Should().Be("ann@example.com");
        h.Email.Sent[0].TextBody.Should().Contain($"userId={user.Id}");
        h.Store.Events.Should().ContainSingle().Which.Type.Should().Be("identity.user-registered.v1");
        h.Store.EmailTokens.Should().ContainSingle(t => t.Purpose == EmailTokenPurpose.Confirm);
    }

    [Fact]
    public async Task F01_AC2_EmailDifferentCase_ReturnsEmailTaken()
    {
        var h = new Harness();
        await h.RegisteredUser("ann@example.com");

        var again = await h.Register.Handle(new RegisterCommand("ANN@Example.COM", Harness.Password, "Other"), default);

        again.IsFailure.Should().BeTrue();
        again.Error.Code.Should().Be("IDENTITY_EMAIL_TAKEN");
        h.Store.Users.Should().HaveCount(1);
    }

    [Fact]
    public async Task F01_BR01_Register_PersistsHashNotPlaintext()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();

        user.PasswordHash.Should().NotBe(Harness.Password);
    }

    [Theory]
    [InlineData("short1")]            // < 8
    [InlineData("allletters")]        // no digit
    [InlineData("12345678")]          // no letter
    [InlineData("")]                  // empty
    public void F01_BR02_WeakPasswords_AreRejected_WithWeakPasswordCode(string password)
    {
        var result = new RegisterValidator().Validate(new RegisterCommand("a@b.co", password, "A"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == IdentityErrors.WeakPasswordCode);
    }

    [Fact]
    public void F01_BR02_PasswordOver128Chars_IsRejected()
    {
        var result = new RegisterValidator().Validate(new RegisterCommand("a@b.co", new string('a', 120) + "123456789", "A"));

        result.Errors.Should().Contain(e => e.ErrorCode == IdentityErrors.WeakPasswordCode);
    }

    [Fact]
    public void F01_BR02_ValidPassword_Passes()
    {
        new RegisterValidator().Validate(new RegisterCommand("a@b.co", "abcdefg1", "A")).IsValid.Should().BeTrue();
    }
}

public class LoginTests
{
    [Fact]
    public async Task F01_AC1_Login_ReturnsTokensAndUser_ForUnconfirmedUser()
    {
        var h = new Harness();
        await h.RegisteredUser();

        var login = await h.LoggedIn();

        login.AccessToken.Should().NotBeNullOrEmpty();
        login.RefreshToken.Should().NotBeNullOrEmpty();
        login.ExpiresIn.Should().Be(900);
        login.User.Roles.Should().Equal(RoleNames.Customer); // BR-04: unconfirmed users can log in
        h.Store.RefreshTokens.Should().ContainSingle().Which.TokenHash.Should().Be(SecureToken.Hash(login.RefreshToken));
    }

    [Fact]
    public async Task F01_AC3_FiveWrongPasswords_ThenCorrectOne_ReturnsLockedOut_Until15MinutesPass()
    {
        var h = new Harness();
        await h.RegisteredUser();

        for (var i = 0; i < 5; i++)
        {
            var wrong = await h.Login.Handle(new LoginCommand("ann@example.com", "wrong-pass1", h.Client), default);
            wrong.Error.Code.Should().Be("IDENTITY_INVALID_CREDENTIALS");
        }

        var locked = await h.Login.Handle(new LoginCommand("ann@example.com", Harness.Password, h.Client), default);
        locked.Error.Code.Should().Be("IDENTITY_LOCKED_OUT");

        h.Time.Advance(TimeSpan.FromMinutes(14));
        (await h.Login.Handle(new LoginCommand("ann@example.com", Harness.Password, h.Client), default))
            .Error.Code.Should().Be("IDENTITY_LOCKED_OUT");

        h.Time.Advance(TimeSpan.FromMinutes(1));
        (await h.Login.Handle(new LoginCommand("ann@example.com", Harness.Password, h.Client), default))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task F01_BR03_SuccessfulLogin_ResetsTheFailureCounter()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        for (var i = 0; i < 4; i++)
            await h.Login.Handle(new LoginCommand("ann@example.com", "wrong-pass1", h.Client), default);

        await h.LoggedIn();

        user.FailedLoginCount.Should().Be(0);
    }

    [Fact]
    public async Task F01_BR07_UnknownEmail_ReturnsSameErrorAsWrongPassword()
    {
        var h = new Harness();
        await h.RegisteredUser();

        var unknown = await h.Login.Handle(new LoginCommand("nobody@example.com", "whatever1", h.Client), default);
        var wrong = await h.Login.Handle(new LoginCommand("ann@example.com", "whatever1", h.Client), default);

        unknown.Error.Should().Be(wrong.Error);
    }

    [Fact]
    public async Task F01_BR07_UnknownEmail_AlsoLocksAfterFiveAttempts_SoLockoutDoesNotRevealExistence()
    {
        var h = new Harness();

        for (var i = 0; i < 5; i++)
            await h.Login.Handle(new LoginCommand("nobody@example.com", "whatever1", h.Client), default);

        var sixth = await h.Login.Handle(new LoginCommand("nobody@example.com", "whatever1", h.Client), default);
        sixth.Error.Code.Should().Be("IDENTITY_LOCKED_OUT");
    }

    [Fact]
    public async Task F01_BR03_IpThrottle_Blocks_After20Failures()
    {
        var h = new Harness();
        h.Throttle.Ip["10.0.0.1"] = 20;

        var result = await h.Login.Handle(new LoginCommand("ann@example.com", Harness.Password, h.Client), default);

        result.Error.Code.Should().Be("IDENTITY_TOO_MANY_ATTEMPTS");
    }

    [Fact]
    public async Task F01_US018_AdminLockedUser_CannotLogin()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        user.Lock(h.Time.GetUtcNow().UtcDateTime);

        var result = await h.Login.Handle(new LoginCommand("ann@example.com", Harness.Password, h.Client), default);

        result.Error.Code.Should().Be("IDENTITY_LOCKED_OUT");
    }
}

public class RefreshTests
{
    [Fact]
    public async Task F01_BR05_Refresh_RotatesToken_AndIssuesNewPair()
    {
        var h = new Harness();
        await h.RegisteredUser();
        var login = await h.LoggedIn();

        var refreshed = await h.Refresh.Handle(new RefreshCommand(login.RefreshToken, h.Client), default);

        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.RefreshToken.Should().NotBe(login.RefreshToken);
        h.Store.RefreshTokens.Should().HaveCount(2);
        h.Store.RefreshTokens.Count(t => !t.IsRevoked).Should().Be(1);
    }

    [Fact]
    public async Task F01_AC4_ReusingRotatedToken_Returns401Reused_AndRevokesTheSuccessorToo()
    {
        var h = new Harness();
        await h.RegisteredUser();
        var r1 = (await h.LoggedIn()).RefreshToken;
        var r2 = (await h.Refresh.Handle(new RefreshCommand(r1, h.Client), default)).Value.RefreshToken;

        var reuse = await h.Refresh.Handle(new RefreshCommand(r1, h.Client), default);

        reuse.Error.Code.Should().Be("IDENTITY_REFRESH_REUSED");
        reuse.Error.Type.Should().Be(BuildingBlocks.Common.ErrorType.Unauthorized);
        h.Store.RefreshTokens.Should().OnlyContain(t => t.IsRevoked);

        var useR2 = await h.Refresh.Handle(new RefreshCommand(r2, h.Client), default);
        useR2.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task F01_AC4_Reuse_OnlyAffectsThatFamily_OtherDevicesStayLoggedIn()
    {
        var h = new Harness();
        await h.RegisteredUser();
        var deviceA = (await h.LoggedIn()).RefreshToken;
        var deviceB = (await h.LoggedIn()).RefreshToken;
        await h.Refresh.Handle(new RefreshCommand(deviceA, h.Client), default);

        await h.Refresh.Handle(new RefreshCommand(deviceA, h.Client), default); // reuse on A

        (await h.Refresh.Handle(new RefreshCommand(deviceB, h.Client), default)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task F01_BR05_ExpiredRefreshToken_IsRejected()
    {
        var h = new Harness();
        await h.RegisteredUser();
        var login = await h.LoggedIn();

        h.Time.Advance(TimeSpan.FromDays(7).Add(TimeSpan.FromSeconds(1)));
        var result = await h.Refresh.Handle(new RefreshCommand(login.RefreshToken, h.Client), default);

        result.Error.Code.Should().Be("IDENTITY_REFRESH_INVALID");
    }

    [Fact]
    public async Task F01_BR05_Refresh_AfterThirtyDaysOfRotation_IsRejected()
    {
        var h = new Harness();
        await h.RegisteredUser();
        var token = (await h.LoggedIn()).RefreshToken;

        for (var i = 0; i < 4; i++)
        {
            h.Time.Advance(TimeSpan.FromDays(6));
            token = (await h.Refresh.Handle(new RefreshCommand(token, h.Client), default)).Value.RefreshToken;
        }

        h.Time.Advance(TimeSpan.FromDays(7)); // day 31
        (await h.Refresh.Handle(new RefreshCommand(token, h.Client), default)).Error.Code.Should().Be("IDENTITY_REFRESH_INVALID");
    }

    [Fact]
    public async Task F01_BR09_LockedUser_CannotRefresh_AndFamilyIsRevoked()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        var login = await h.LoggedIn();
        user.Lock(h.Time.GetUtcNow().UtcDateTime);

        var result = await h.Refresh.Handle(new RefreshCommand(login.RefreshToken, h.Client), default);

        result.Error.Code.Should().Be("IDENTITY_REFRESH_INVALID");
        h.Store.RefreshTokens.Should().OnlyContain(t => t.IsRevoked);
    }

    [Fact]
    public async Task F01_AC4_UnknownRefreshToken_IsRejected()
    {
        var h = new Harness();

        var result = await h.Refresh.Handle(new RefreshCommand("not-a-token", h.Client), default);

        result.Error.Code.Should().Be("IDENTITY_REFRESH_INVALID");
    }
}

public class LogoutTests
{
    [Fact]
    public async Task F01_AC5_Logout_BlacklistsJti_ForRemainingLifetime_AndRevokesFamily()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        var login = await h.LoggedIn();
        var now = h.Time.GetUtcNow().UtcDateTime;

        var result = await h.Logout.Handle(new LogoutCommand(user.Id, login.RefreshToken, false, "jti-1", now.AddMinutes(12)), default);

        result.IsSuccess.Should().BeTrue();
        h.Revocation.Revoked["jti-1"].Should().Be(TimeSpan.FromMinutes(12));
        h.Store.RefreshTokens.Should().OnlyContain(t => t.IsRevoked);
    }

    [Fact]
    public async Task F01_US015_LogoutAllDevices_RevokesEverySession()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        await h.LoggedIn();
        await h.LoggedIn();

        await h.Logout.Handle(new LogoutCommand(user.Id, null, true, "jti", h.Time.GetUtcNow().UtcDateTime.AddMinutes(5)), default);

        h.Store.RefreshTokens.Should().HaveCount(2).And.OnlyContain(t => t.IsRevoked);
    }

    [Fact]
    public async Task F01_US015_Logout_IgnoresRefreshTokenOfAnotherUser()
    {
        var h = new Harness();
        await h.RegisteredUser("ann@example.com");
        var bob = await h.RegisteredUser("bob@example.com");
        var annLogin = await h.LoggedIn("ann@example.com");

        await h.Logout.Handle(new LogoutCommand(bob.Id, annLogin.RefreshToken, false, null, null), default);

        h.Store.RefreshTokens.Should().OnlyContain(t => !t.IsRevoked);
    }
}

public class PasswordFlowTests
{
    [Fact]
    public async Task F01_AC7_ForgotPassword_ReturnsSameResult_ForKnownAndUnknownEmails()
    {
        var h = new Harness();
        await h.RegisteredUser();
        h.Email.Sent.Clear();
        h.Store.Events.Clear();

        var known = await h.Forgot.Handle(new ForgotPasswordCommand("ann@example.com"), default);
        var unknown = await h.Forgot.Handle(new ForgotPasswordCommand("ghost@example.com"), default);

        known.IsSuccess.Should().BeTrue();
        unknown.IsSuccess.Should().BeTrue();
        known.Error.Should().Be(unknown.Error);
        h.Email.Sent.Should().ContainSingle(); // only the real account got mail
        h.Store.Events.Should().ContainSingle().Which.Type.Should().Be("identity.password-reset-requested.v1");
    }

    [Fact]
    public async Task F01_US014_ResetPassword_SetsNewPassword_RevokesSessions_AndTokenIsSingleUse()
    {
        var h = new Harness();
        await h.RegisteredUser();
        await h.LoggedIn();
        await h.Forgot.Handle(new ForgotPasswordCommand("ann@example.com"), default);
        var token = ExtractToken(h.Email.Sent.Last().TextBody);

        var reset = await h.Reset.Handle(new ResetPasswordCommand("ann@example.com", token, "N3wPassword"), default);

        reset.IsSuccess.Should().BeTrue();
        h.Store.RefreshTokens.Should().OnlyContain(t => t.IsRevoked);
        (await h.Login.Handle(new LoginCommand("ann@example.com", "N3wPassword", h.Client), default)).IsSuccess.Should().BeTrue();
        (await h.Reset.Handle(new ResetPasswordCommand("ann@example.com", token, "An0therOne"), default)).Error.Code
            .Should().Be("IDENTITY_TOKEN_INVALID");
    }

    [Fact]
    public async Task F01_US014_ResetPassword_TokenExpiresAfter30Minutes()
    {
        var h = new Harness();
        await h.RegisteredUser();
        await h.Forgot.Handle(new ForgotPasswordCommand("ann@example.com"), default);
        var token = ExtractToken(h.Email.Sent.Last().TextBody);

        h.Time.Advance(TimeSpan.FromMinutes(31));
        var reset = await h.Reset.Handle(new ResetPasswordCommand("ann@example.com", token, "N3wPassword"), default);

        reset.Error.Code.Should().Be("IDENTITY_TOKEN_INVALID");
    }

    [Fact]
    public async Task F01_US014_NewResetRequest_InvalidatesThePreviousLink()
    {
        var h = new Harness();
        await h.RegisteredUser();
        await h.Forgot.Handle(new ForgotPasswordCommand("ann@example.com"), default);
        var first = ExtractToken(h.Email.Sent.Last().TextBody);
        await h.Forgot.Handle(new ForgotPasswordCommand("ann@example.com"), default);

        (await h.Reset.Handle(new ResetPasswordCommand("ann@example.com", first, "N3wPassword"), default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task F01_US013_ConfirmEmail_MarksUserConfirmed_AndTokenIsSingleUse()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        var token = ExtractToken(h.Email.Sent.Single().TextBody);

        var result = await h.Confirm.Handle(new ConfirmEmailCommand(user.Id, token), default);

        result.IsSuccess.Should().BeTrue();
        user.EmailConfirmed.Should().BeTrue();
        (await h.Confirm.Handle(new ConfirmEmailCommand(user.Id, token), default)).Error.Code.Should().Be("IDENTITY_TOKEN_INVALID");
    }

    [Fact]
    public async Task F01_US013_ConfirmEmail_WrongToken_IsRejected()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();

        (await h.Confirm.Handle(new ConfirmEmailCommand(user.Id, "bogus"), default)).Error.Code.Should().Be("IDENTITY_TOKEN_INVALID");
    }

    [Fact]
    public async Task F01_US016_ChangePassword_RequiresCurrentPassword()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        var handler = new ChangePasswordHandler(h.Store, h.Hasher, h.Store, h.Time);

        (await handler.Handle(new ChangePasswordCommand(user.Id, "wrong-pass1", "N3wPassword"), default)).Error.Code
            .Should().Be("IDENTITY_INVALID_CURRENT_PASSWORD");
        (await handler.Handle(new ChangePasswordCommand(user.Id, Harness.Password, "N3wPassword"), default)).IsSuccess.Should().BeTrue();
        (await h.Login.Handle(new LoginCommand("ann@example.com", "N3wPassword", h.Client), default)).IsSuccess.Should().BeTrue();
    }

    private static string ExtractToken(string body)
    {
        var start = body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
        var end = body.IndexOfAny(['&', '\n', ' '], start);
        return Uri.UnescapeDataString(end < 0 ? body[start..] : body[start..end]);
    }
}

public class AdminTests
{
    [Fact]
    public async Task F01_US018_LockUser_RevokesSessions_AndWritesUserLockedEvent()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        await h.LoggedIn();
        var adminId = Guid.NewGuid();
        h.Store.Events.Clear();

        var result = await h.Lock.Handle(new LockUserCommand(user.Id, adminId), default);

        result.IsSuccess.Should().BeTrue();
        user.Status.Should().Be(UserStatus.Locked);
        h.Store.RefreshTokens.Should().OnlyContain(t => t.IsRevoked);
        h.Store.Events.Should().ContainSingle().Which.Type.Should().Be("identity.user-locked.v1");
    }

    [Fact]
    public async Task F01_US018_Admin_CannotLockThemselves()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();

        var result = await h.Lock.Handle(new LockUserCommand(user.Id, user.Id), default);

        result.Error.Code.Should().Be("IDENTITY_CANNOT_LOCK_SELF");
    }

    [Fact]
    public async Task F01_US017_SetRoles_RejectsUnknownRole()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        var handler = new SetRolesHandler(h.Store, h.Store, h.Store, h.Time);

        var result = await handler.Handle(new SetRolesCommand(user.Id, ["Wizard"]), default);

        result.Error.Code.Should().Be("IDENTITY_UNKNOWN_ROLE");
    }

    [Fact]
    public async Task F01_US017_SetRoles_AssignsAdmin()
    {
        var h = new Harness();
        var user = await h.RegisteredUser();
        var handler = new SetRolesHandler(h.Store, h.Store, h.Store, h.Time);

        (await handler.Handle(new SetRolesCommand(user.Id, ["admin"]), default)).IsSuccess.Should().BeTrue();

        user.RoleNames.Should().Equal(RoleNames.Admin);
    }
}
