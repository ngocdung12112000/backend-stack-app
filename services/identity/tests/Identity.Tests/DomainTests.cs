using Identity.Domain;

namespace Identity.Tests;

public class RefreshTokenTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void F01_BR05_Rotate_CreatesSuccessorInSameFamily_AndRevokesOld()
    {
        var first = RefreshToken.Issue(Guid.NewGuid(), "h1", T0, "1.1.1.1", "ua");

        var second = first.Rotate("h2", T0.AddMinutes(10), "1.1.1.1", "ua");

        second.FamilyId.Should().Be(first.FamilyId);
        second.UserId.Should().Be(first.UserId);
        second.TokenHash.Should().Be("h2");
        first.IsRevoked.Should().BeTrue();
        first.WasRotated.Should().BeTrue();
        first.ReplacedById.Should().Be(second.Id);
        second.IsActive(T0.AddMinutes(10)).Should().BeTrue();
    }

    [Fact]
    public void F01_BR05_RefreshToken_ExpiresAfterSevenDays_SlidingOnRotation()
    {
        var first = RefreshToken.Issue(Guid.NewGuid(), "h1", T0, null, null);
        first.ExpiresAt.Should().Be(T0.AddDays(7));

        var second = first.Rotate("h2", T0.AddDays(6), null, null);

        second.ExpiresAt.Should().Be(T0.AddDays(13)); // slid forward
    }

    [Fact]
    public void F01_BR05_Family_NeverOutlivesThirtyDayAbsoluteCap()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "h0", T0, null, null);
        for (var day = 6; day <= 28; day += 6)
            token = token.Rotate($"h{day}", T0.AddDays(day), null, null);

        token.ExpiresAt.Should().Be(T0.AddDays(30));
        token.IsActive(T0.AddDays(30).AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void F01_BR06_Rotate_OnRevokedToken_Throws()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "h1", T0, null, null);
        token.Rotate("h2", T0.AddMinutes(1), null, null);

        var again = () => token.Rotate("h3", T0.AddMinutes(2), null, null);

        again.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Revoke_IsIdempotent_AndKeepsFirstTimestamp()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "h1", T0, null, null);
        token.Revoke(T0.AddMinutes(1));
        token.Revoke(T0.AddMinutes(9));

        token.RevokedAt.Should().Be(T0.AddMinutes(1));
        token.WasRotated.Should().BeFalse();
    }
}

public class UserTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Role Customer = new(IdentitySeed.CustomerRoleId, RoleNames.Customer);

    private static User NewUser() => User.Register("Ann@Example.com", "Ann", "hash", Customer, T0);

    [Fact]
    public void F01_BR01_Register_NormalizesEmailToUpperInvariant()
    {
        var user = NewUser();

        user.NormalizedEmail.Should().Be("ANN@EXAMPLE.COM");
        User.NormalizeEmail("  ann@example.COM ").Should().Be(user.NormalizedEmail);
    }

    [Fact]
    public void F01_BR08_Register_AssignsCustomerRole()
    {
        NewUser().RoleNames.Should().Equal(RoleNames.Customer);
    }

    [Fact]
    public void F01_BR03_FiveFailures_LockForFifteenMinutes()
    {
        var user = NewUser();

        for (var i = 1; i <= 4; i++)
            user.RegisterFailedLogin(T0, 5, TimeSpan.FromMinutes(15)).Should().Be(FailedLoginOutcome.Counted);
        user.IsLockedOut(T0).Should().BeFalse();

        user.RegisterFailedLogin(T0, 5, TimeSpan.FromMinutes(15)).Should().Be(FailedLoginOutcome.LockedOut);

        user.IsLockedOut(T0.AddMinutes(14)).Should().BeTrue();
        user.IsLockedOut(T0.AddMinutes(15)).Should().BeFalse();
        user.LockoutEnd.Should().Be(T0.AddMinutes(15));
    }

    [Fact]
    public void F01_BR03_SuccessfulLogin_ResetsFailureCounter()
    {
        var user = NewUser();
        for (var i = 0; i < 4; i++) user.RegisterFailedLogin(T0, 5, TimeSpan.FromMinutes(15));

        user.RegisterSuccessfulLogin(T0);

        user.FailedLoginCount.Should().Be(0);
        user.RegisterFailedLogin(T0, 5, TimeSpan.FromMinutes(15)).Should().Be(FailedLoginOutcome.Counted);
    }

    [Fact]
    public void F01_US018_AdminLock_StaysUntilUnlocked()
    {
        var user = NewUser();
        user.Lock(T0);
        user.IsLockedOut(T0.AddDays(365)).Should().BeTrue();

        user.Unlock(T0.AddDays(1));

        user.IsLockedOut(T0.AddDays(1)).Should().BeFalse();
        user.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public void F01_US017_SetRoles_ReplacesRoleSet()
    {
        var user = NewUser();
        var admin = new Role(IdentitySeed.AdminRoleId, RoleNames.Admin);

        user.SetRoles([admin], T0);

        user.RoleNames.Should().Equal(RoleNames.Admin);
    }
}

public class EmailTokenTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Token_IsSingleUse_AndExpires()
    {
        var token = EmailToken.Create(Guid.NewGuid(), EmailTokenPurpose.Reset, "h", T0, TimeSpan.FromMinutes(30));

        token.IsUsable(T0.AddMinutes(29)).Should().BeTrue();
        token.IsUsable(T0.AddMinutes(30)).Should().BeFalse();

        token.MarkUsed(T0);
        token.IsUsable(T0.AddMinutes(1)).Should().BeFalse();
    }
}
