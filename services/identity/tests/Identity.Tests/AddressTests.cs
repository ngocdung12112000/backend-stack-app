using Identity.Application;
using Identity.Application.Addresses;
using Identity.Application.Auth;
using Identity.Application.Users;
using Identity.Domain;

namespace Identity.Tests;

public class AddressBookTests
{
    private static readonly DateTime T0 = new(2026, 11, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.NewGuid();

    private static AddressDetails Details(string name = "Ann") =>
        new(name, "+84901234567", "1 Main St", null, null, null, "Hanoi", "VN", null);

    [Fact]
    public void F02_AC1_FirstAddress_BecomesDefaultAutomatically()
    {
        var book = new AddressBook(UserId, []);

        var first = book.Add(Details(), T0)!;
        var second = book.Add(Details("Bob"), T0)!;

        first.IsDefault.Should().BeTrue();
        second.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void F02_AC2_SetDefault_UnsetsThePreviousDefault_LeavingExactlyOne()
    {
        var book = new AddressBook(UserId, []);
        var a = book.Add(Details("A"), T0)!;
        var b = book.Add(Details("B"), T0)!;

        book.SetDefault(b.Id, T0.AddMinutes(1)).Should().BeTrue();

        a.IsDefault.Should().BeFalse();
        b.IsDefault.Should().BeTrue();
        book.Addresses.Count(x => x.IsDefault).Should().Be(1);
    }

    [Fact]
    public void F02_AC3_EleventhAddress_IsRejected()
    {
        var book = new AddressBook(UserId, []);
        for (var i = 0; i < AddressBook.MaxAddresses; i++) book.Add(Details($"A{i}"), T0).Should().NotBeNull();

        book.Add(Details("extra"), T0).Should().BeNull();
        book.Addresses.Should().HaveCount(10);
    }

    [Fact]
    public void F02_BR02_DeletingTheDefault_PromotesTheMostRecentlyUpdatedRemaining()
    {
        var book = new AddressBook(UserId, []);
        var a = book.Add(Details("A"), T0)!;
        var b = book.Add(Details("B"), T0.AddMinutes(1))!;
        var c = book.Add(Details("C"), T0.AddMinutes(2))!;
        book.Replace(b.Id, Details("B2"), T0.AddMinutes(10)); // b is now the most recently updated

        book.Remove(a.Id, T0.AddMinutes(11)).Should().Be(a);

        b.IsDefault.Should().BeTrue();
        c.IsDefault.Should().BeFalse();
        book.Addresses.Count(x => x.IsDefault).Should().Be(1);
    }

    [Fact]
    public void F02_BR02_DeletingANonDefault_KeepsTheDefault_AndLastDeleteLeavesEmptyBook()
    {
        var book = new AddressBook(UserId, []);
        var a = book.Add(Details("A"), T0)!;
        var b = book.Add(Details("B"), T0)!;

        book.Remove(b.Id, T0);
        a.IsDefault.Should().BeTrue();
        book.Remove(a.Id, T0);
        book.Addresses.Should().BeEmpty();
        book.Remove(Guid.NewGuid(), T0).Should().BeNull();
    }

    [Fact]
    public void F02_BR02_Replace_DoesNotChangeTheDefaultFlag()
    {
        var book = new AddressBook(UserId, []);
        var a = book.Add(Details("A"), T0)!;

        book.Replace(a.Id, Details("A2"), T0.AddMinutes(1)).Should().BeTrue();

        a.RecipientName.Should().Be("A2");
        a.IsDefault.Should().BeTrue();
        book.Replace(Guid.NewGuid(), Details(), T0).Should().BeFalse();
    }
}

public class AddressValidatorTests
{
    private static AddressInput Valid() => new("Ann", "+84901234567", "1 Main St", null, null, null, "Hanoi", "VN", null);
    private readonly AddressInputValidator _validator = new();

    [Fact]
    public void F02_BR04_ValidAddress_Passes()
    {
        _validator.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("0901234567")]
    [InlineData("+84 12")]
    [InlineData("+1555")]
    [InlineData("not a phone")]
    public void F02_BR03_Phone_MustBeValidE164(string phone)
    {
        var result = _validator.Validate(Valid() with { Phone = phone });

        result.Errors.Should().Contain(e => e.PropertyName == nameof(AddressInput.Phone));
    }

    [Fact]
    public void F02_BR04_RequiredAndLengthRules()
    {
        var result = _validator.Validate(new AddressInput("", "", "", new string('x', 201), null, null, "", "VNM", new string('9', 21)));

        result.Errors.Select(e => e.PropertyName).Should().Contain([
            nameof(AddressInput.RecipientName), nameof(AddressInput.Phone), nameof(AddressInput.Line1), nameof(AddressInput.Line2),
            nameof(AddressInput.City), nameof(AddressInput.CountryCode), nameof(AddressInput.PostalCode)]);
        _validator.Validate(Valid() with { RecipientName = new string('n', 101) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void F02_BR03_ToDetails_NormalisesPhoneAndCountry()
    {
        var details = (Valid() with { Phone = " +84 901 234 567 ", CountryCode = "vn", Line2 = "  " }).ToDetails();

        details.Phone.Should().Be("+84901234567");
        details.CountryCode.Should().Be("VN");
        details.Line2.Should().BeNull();
    }
}

public class AddressHandlerTests
{
    private static AddressInput Input(string name = "Ann") => new(name, "+84901234567", "1 Main St", null, null, null, "Hanoi", "VN", null);

    [Fact]
    public async Task F02_AC4_OtherUsersAddress_IsNotFound_ForReplaceDeleteAndSetDefault()
    {
        var h = new Harness();
        var ann = await h.RegisteredUser("ann@example.com");
        var bob = await h.RegisteredUser("bob@example.com");
        var annAddress = (await new CreateAddressHandler(h.Store, h.Store, h.Time)
            .Handle(new CreateAddressCommand(ann.Id, Input()), default)).Value;

        (await new ReplaceAddressHandler(h.Store, h.Store, h.Time).Handle(new ReplaceAddressCommand(bob.Id, annAddress.Id, Input("X")), default))
            .Error.Code.Should().Be("IDENTITY_ADDRESS_NOT_FOUND");
        (await new DeleteAddressHandler(h.Store, h.Store, h.Time).Handle(new DeleteAddressCommand(bob.Id, annAddress.Id), default))
            .Error.Code.Should().Be("IDENTITY_ADDRESS_NOT_FOUND");
        (await new SetDefaultAddressHandler(h.Store, h.Store, h.Time).Handle(new SetDefaultAddressCommand(bob.Id, annAddress.Id), default))
            .Error.Code.Should().Be("IDENTITY_ADDRESS_NOT_FOUND");
        (await new ListAddressesHandler(h.Store).Handle(new ListAddressesQuery(bob.Id), default)).Value.Should().BeEmpty();
        h.Store.Addresses.Should().ContainSingle().Which.RecipientName.Should().Be("Ann");
    }

    [Fact]
    public async Task F02_AC3_EleventhAddress_ReturnsConflictCode()
    {
        var h = new Harness();
        var ann = await h.RegisteredUser();
        var create = new CreateAddressHandler(h.Store, h.Store, h.Time);
        for (var i = 0; i < 10; i++) (await create.Handle(new CreateAddressCommand(ann.Id, Input($"A{i}")), default)).IsSuccess.Should().BeTrue();

        var result = await create.Handle(new CreateAddressCommand(ann.Id, Input("11th")), default);

        result.Error.Code.Should().Be("IDENTITY_ADDRESS_LIMIT_REACHED");
        result.Error.Type.Should().Be(BuildingBlocks.Common.ErrorType.Conflict);
    }

    [Fact]
    public async Task F02_US023_List_ReturnsDefaultFirst()
    {
        var h = new Harness();
        var ann = await h.RegisteredUser();
        var create = new CreateAddressHandler(h.Store, h.Store, h.Time);
        await create.Handle(new CreateAddressCommand(ann.Id, Input("A")), default);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        var b = (await create.Handle(new CreateAddressCommand(ann.Id, Input("B")), default)).Value;
        await new SetDefaultAddressHandler(h.Store, h.Store, h.Time).Handle(new SetDefaultAddressCommand(ann.Id, b.Id), default);

        var list = (await new ListAddressesHandler(h.Store).Handle(new ListAddressesQuery(ann.Id), default)).Value;

        list.Select(a => a.RecipientName).Should().Equal("B", "A");
        list[0].IsDefault.Should().BeTrue();
    }
}

public class ProfileAndDeletionTests
{
    private static AddressInput Input() => new("Ann", "+84901234567", "1 Main St", null, null, null, "Hanoi", "VN", null);

    private static DeleteAccountHandler Deleter(Harness h) =>
        new(h.Store, h.Store, h.Store, h.Store, h.Hasher, h.Revocation, h.Store, h.Store, h.Time);

    [Fact]
    public async Task F02_US021_UpdateProfile_ChangesOnlyProvidedFields_AndNormalisesPhone()
    {
        var h = new Harness();
        var ann = await h.RegisteredUser();
        var handler = new UpdateProfileHandler(h.Store, h.Store, h.Time);

        var avatar = Guid.NewGuid();
        var result = await handler.Handle(new UpdateProfileCommand(ann.Id, null, "+84 901 234 567", avatar), default);

        result.Value.FullName.Should().Be("Ann Example");
        result.Value.Phone.Should().Be("+84901234567");
        result.Value.AvatarAssetId.Should().Be(avatar);

        var cleared = await handler.Handle(new UpdateProfileCommand(ann.Id, "Ann B", "", Guid.Empty), default);
        cleared.Value.Phone.Should().BeNull();
        cleared.Value.AvatarAssetId.Should().BeNull();
        cleared.Value.FullName.Should().Be("Ann B");
    }

    [Fact]
    public void F02_US021_UpdateProfileValidator_RejectsBadInput()
    {
        var v = new UpdateProfileValidator();
        var id = Guid.NewGuid();

        v.Validate(new UpdateProfileCommand(id, null, null, null)).IsValid.Should().BeFalse();
        v.Validate(new UpdateProfileCommand(id, "", null, null)).IsValid.Should().BeFalse();
        v.Validate(new UpdateProfileCommand(id, null, "0901234567", null)).IsValid.Should().BeFalse();
        v.Validate(new UpdateProfileCommand(id, new string('a', 201), null, null)).IsValid.Should().BeFalse();
        v.Validate(new UpdateProfileCommand(id, "Ann", "+84901234567", null)).IsValid.Should().BeTrue();
        v.Validate(new UpdateProfileCommand(id, null, "", null)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task F02_BR06_DeleteAccount_AnonymisesRevokesAndPublishesEvent()
    {
        var h = new Harness();
        var ann = await h.RegisteredUser();
        await h.LoggedIn();
        await new CreateAddressHandler(h.Store, h.Store, h.Time).Handle(new CreateAddressCommand(ann.Id, Input()), default);
        var exp = h.Time.GetUtcNow().UtcDateTime.AddMinutes(10);

        var result = await Deleter(h).Handle(new DeleteAccountCommand(ann.Id, Harness.Password, "jti-1", exp), default);

        result.IsSuccess.Should().BeTrue();
        ann.Status.Should().Be(UserStatus.Deleted);
        ann.Email.Should().Be($"deleted+{ann.Id}@invalid");
        ann.FullName.Should().BeNull();
        ann.Phone.Should().BeNull();
        h.Store.Addresses.Should().BeEmpty();
        h.Store.RefreshTokens.Should().OnlyContain(t => t.IsRevoked);
        h.Revocation.Revoked.Should().ContainKey("jti-1");
        h.Store.Events.OfType<UserDeletedV1>().Should().ContainSingle().Which.UserId.Should().Be(ann.Id);
    }

    [Fact]
    public async Task F02_AC5_DeleteAccount_WrongPassword_ChangesNothing_AndLoginFailsAfterDeletion()
    {
        var h = new Harness();
        var ann = await h.RegisteredUser();
        var handler = Deleter(h);

        var wrong = await handler.Handle(new DeleteAccountCommand(ann.Id, "nope", null, null), default);
        wrong.Error.Code.Should().Be("IDENTITY_INVALID_CURRENT_PASSWORD");
        ann.Status.Should().Be(UserStatus.Active);
        h.Store.Events.OfType<UserDeletedV1>().Should().BeEmpty();

        (await handler.Handle(new DeleteAccountCommand(ann.Id, Harness.Password, null, null), default)).IsSuccess.Should().BeTrue();
        (await h.Login.Handle(new LoginCommand("ann@example.com", Harness.Password, h.Client), default)).IsSuccess.Should().BeFalse();
        (await handler.Handle(new DeleteAccountCommand(ann.Id, Harness.Password, null, null), default)).Error.Code
            .Should().Be("IDENTITY_USER_NOT_FOUND");
    }
}
