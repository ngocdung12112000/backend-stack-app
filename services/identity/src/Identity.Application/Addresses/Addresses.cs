using BuildingBlocks.Common;
using FluentValidation;
using Identity.Application.Abstractions;
using Identity.Domain;
using MediatR;
using PhoneNumbers;

namespace Identity.Application.Addresses;

/// <summary>BR-03: phone numbers are E.164 (leading "+", valid per libphonenumber) and stored in that canonical form.</summary>
public static class PhoneRules
{
    public const string InvalidMessage = "Phone must be a valid number in international E.164 format, e.g. +84901234567.";

    public static bool TryNormalize(string? value, out string e164)
    {
        e164 = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (trimmed[0] != '+') return false;
        try
        {
            var util = PhoneNumberUtil.GetInstance();
            var number = util.Parse(trimmed, null);
            if (!util.IsValidNumber(number)) return false;
            e164 = util.Format(number, PhoneNumberFormat.E164);
            return true;
        }
        catch (NumberParseException)
        {
            return false;
        }
    }

    public static IRuleBuilderOptions<T, string?> MustBeE164<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(p => TryNormalize(p, out _)).WithMessage(InvalidMessage);
}

/// <summary>Request payload shared by create (POST) and replace (PUT).</summary>
public sealed record AddressInput(
    string RecipientName, string Phone, string Line1, string? Line2, string? Ward, string? District,
    string City, string CountryCode, string? PostalCode)
{
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Trims, upper-cases the country and canonicalises the phone. Call only after validation.</summary>
    public AddressDetails ToDetails()
    {
        PhoneRules.TryNormalize(Phone, out var phone);
        return new AddressDetails(
            RecipientName.Trim(), phone, Line1.Trim(), Clean(Line2), Clean(Ward), Clean(District),
            City.Trim(), CountryCode.Trim().ToUpperInvariant(), Clean(PostalCode));
    }
}

public sealed class AddressInputValidator : AbstractValidator<AddressInput>
{
    public AddressInputValidator()
    {
        RuleFor(x => x.RecipientName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Phone).NotEmpty();
        RuleFor(x => x.Phone).MustBeE164().When(x => !string.IsNullOrWhiteSpace(x.Phone));
        RuleFor(x => x.Line1).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Line2).MaximumLength(200);
        RuleFor(x => x.Ward).MaximumLength(100);
        RuleFor(x => x.District).MaximumLength(100);
        RuleFor(x => x.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.CountryCode).NotEmpty();
        RuleFor(x => x.CountryCode).Matches("^[A-Za-z]{2}$").When(x => !string.IsNullOrEmpty(x.CountryCode))
            .WithMessage("Country code must be an ISO 3166-1 alpha-2 code, e.g. VN.");
        RuleFor(x => x.PostalCode).MaximumLength(20);
    }
}

public sealed record AddressDto(
    Guid Id, string RecipientName, string Phone, string Line1, string? Line2, string? Ward, string? District,
    string City, string CountryCode, string? PostalCode, bool IsDefault, DateTime CreatedAt, DateTime UpdatedAt);

public static class AddressMapping
{
    public static AddressDto ToDto(this Address a) =>
        new(a.Id, a.RecipientName, a.Phone, a.Line1, a.Line2, a.Ward, a.District, a.City, a.CountryCode, a.PostalCode,
            a.IsDefault, a.CreatedAt, a.UpdatedAt);
}

// ---------------------------------------------------------------- Queries

public sealed record ListAddressesQuery(Guid UserId) : IRequest<Result<IReadOnlyList<AddressDto>>>;

public sealed class ListAddressesHandler(IAddressRepository addresses)
    : IRequestHandler<ListAddressesQuery, Result<IReadOnlyList<AddressDto>>>
{
    public async Task<Result<IReadOnlyList<AddressDto>>> Handle(ListAddressesQuery q, CancellationToken ct)
    {
        var list = await addresses.ListAsync(q.UserId, ct);
        // Default first, then the most recently updated.
        IReadOnlyList<AddressDto> dtos = list.OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.Id).Select(a => a.ToDto()).ToList();
        return Result.Success(dtos);
    }
}

// ---------------------------------------------------------------- Commands (each one runs in a transaction holding the user row lock)

public sealed record CreateAddressCommand(Guid UserId, AddressInput Address) : IRequest<Result<AddressDto>>;

public sealed class CreateAddressValidator : AbstractValidator<CreateAddressCommand>
{
    public CreateAddressValidator() => RuleFor(x => x.Address).NotNull().SetValidator(new AddressInputValidator());
}

public sealed class CreateAddressHandler(IAddressRepository addresses, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<CreateAddressCommand, Result<AddressDto>>
{
    public Task<Result<AddressDto>> Handle(CreateAddressCommand cmd, CancellationToken ct) =>
        uow.InTransactionAsync(async () =>
        {
            var book = await addresses.LoadForUpdateAsync(cmd.UserId, ct);
            if (book is null) return Result.Failure<AddressDto>(IdentityErrors.UserNotFound);

            var created = book.Add(cmd.Address.ToDetails(), time.GetUtcNow().UtcDateTime);
            if (created is null) return Result.Failure<AddressDto>(IdentityErrors.AddressLimitReached);

            addresses.Add(created);
            await uow.SaveChangesAsync(ct);
            return Result.Success(created.ToDto());
        }, ct);
}

public sealed record ReplaceAddressCommand(Guid UserId, Guid AddressId, AddressInput Address) : IRequest<Result<AddressDto>>;

public sealed class ReplaceAddressValidator : AbstractValidator<ReplaceAddressCommand>
{
    public ReplaceAddressValidator() => RuleFor(x => x.Address).NotNull().SetValidator(new AddressInputValidator());
}

public sealed class ReplaceAddressHandler(IAddressRepository addresses, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<ReplaceAddressCommand, Result<AddressDto>>
{
    public Task<Result<AddressDto>> Handle(ReplaceAddressCommand cmd, CancellationToken ct) =>
        uow.InTransactionAsync(async () =>
        {
            // The book only contains this user's addresses, so someone else's id is simply "not found" (404, never 403).
            var book = await addresses.LoadForUpdateAsync(cmd.UserId, ct);
            if (book is null || !book.Replace(cmd.AddressId, cmd.Address.ToDetails(), time.GetUtcNow().UtcDateTime))
                return Result.Failure<AddressDto>(IdentityErrors.AddressNotFound);

            await uow.SaveChangesAsync(ct);
            return Result.Success(book.Find(cmd.AddressId)!.ToDto());
        }, ct);
}

public sealed record DeleteAddressCommand(Guid UserId, Guid AddressId) : IRequest<Result>;

public sealed class DeleteAddressHandler(IAddressRepository addresses, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<DeleteAddressCommand, Result>
{
    public Task<Result> Handle(DeleteAddressCommand cmd, CancellationToken ct) =>
        uow.InTransactionAsync(async () =>
        {
            var book = await addresses.LoadForUpdateAsync(cmd.UserId, ct);
            var removed = book?.Remove(cmd.AddressId, time.GetUtcNow().UtcDateTime);
            if (removed is null) return Result.Failure(IdentityErrors.AddressNotFound);

            addresses.Remove(removed);
            await uow.SaveChangesAsync(ct);
            return Result.Success();
        }, ct);
}

public sealed record SetDefaultAddressCommand(Guid UserId, Guid AddressId) : IRequest<Result<AddressDto>>;

public sealed class SetDefaultAddressHandler(IAddressRepository addresses, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<SetDefaultAddressCommand, Result<AddressDto>>
{
    public Task<Result<AddressDto>> Handle(SetDefaultAddressCommand cmd, CancellationToken ct) =>
        uow.InTransactionAsync(async () =>
        {
            var book = await addresses.LoadForUpdateAsync(cmd.UserId, ct);
            // AC-2: the old default is unset and the new one set in this one transaction.
            if (book is null || !book.SetDefault(cmd.AddressId, time.GetUtcNow().UtcDateTime))
                return Result.Failure<AddressDto>(IdentityErrors.AddressNotFound);

            await uow.SaveChangesAsync(ct);
            return Result.Success(book.Find(cmd.AddressId)!.ToDto());
        }, ct);
}
