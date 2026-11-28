using BuildingBlocks.Common;
using FluentValidation;
using Identity.Application.Abstractions;
using Identity.Application.Addresses;
using Identity.Domain;
using MediatR;

namespace Identity.Application.Users;

// ---------------------------------------------------------------- US-02.1 profile update

/// <summary>PATCH semantics: a null property is left unchanged. An empty <c>phone</c> and the all-zero <c>avatarAssetId</c> clear the value.</summary>
public sealed record UpdateProfileCommand(Guid UserId, string? FullName, string? Phone, Guid? AvatarAssetId)
    : IRequest<Result<UserProfile>>;

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x).Must(x => x.FullName is not null || x.Phone is not null || x.AvatarAssetId is not null)
            .WithName("request").WithMessage("Provide at least one of fullName, phone or avatarAssetId.");
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200).When(x => x.FullName is not null);
        RuleFor(x => x.Phone).MustBeE164().When(x => !string.IsNullOrEmpty(x.Phone));
    }
}

public sealed class UpdateProfileHandler(IUserRepository users, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<UpdateProfileCommand, Result<UserProfile>>
{
    public async Task<Result<UserProfile>> Handle(UpdateProfileCommand cmd, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(cmd.UserId, ct);
        if (user is null || user.IsDeleted) return Result.Failure<UserProfile>(IdentityErrors.UserNotFound);

        var clearPhone = cmd.Phone is { Length: 0 };
        string? phone = PhoneRules.TryNormalize(cmd.Phone, out var e164) ? e164 : null;
        var clearAvatar = cmd.AvatarAssetId == Guid.Empty;
        user.UpdateProfile(cmd.FullName, phone, clearPhone, clearAvatar ? null : cmd.AvatarAssetId, clearAvatar,
            time.GetUtcNow().UtcDateTime);

        await uow.SaveChangesAsync(ct);
        return Result.Success(user.ToProfile());
    }
}

// ---------------------------------------------------------------- US-02.4 account deletion (BR-06)

public sealed record DeleteAccountCommand(Guid UserId, string Password, string? Jti, DateTime? AccessTokenExpiresAt) : IRequest<Result>;

public sealed class DeleteAccountValidator : AbstractValidator<DeleteAccountCommand>
{
    public DeleteAccountValidator() => RuleFor(x => x.Password).NotEmpty();
}

public sealed class DeleteAccountHandler(
    IUserRepository users, IAddressRepository addresses, IRefreshTokenRepository refreshTokens, IEmailTokenRepository emailTokens,
    IPasswordHasher hasher, ITokenRevocation revocation, IOutbox outbox, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<DeleteAccountCommand, Result>
{
    public async Task<Result> Handle(DeleteAccountCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        var result = await uow.InTransactionAsync(async () =>
        {
            // The same user-row lock the address commands take: no address can be created while the account is being erased.
            var book = await addresses.LoadForUpdateAsync(cmd.UserId, ct);
            var user = await users.GetByIdAsync(cmd.UserId, ct);
            if (book is null || user is null || user.IsDeleted) return Result.Failure(IdentityErrors.UserNotFound);

            if (hasher.Verify(user.PasswordHash, cmd.Password) == PasswordVerification.Failed)
                return Result.Failure(IdentityErrors.InvalidCurrentPassword);

            foreach (var address in book.Addresses.ToList()) addresses.Remove(address);
            user.MarkDeleted("!" + SecureToken.Generate(), now);
            await refreshTokens.RevokeAllForUserAsync(user.Id, now, ct);
            await emailTokens.InvalidateOutstandingAsync(user.Id, EmailTokenPurpose.Confirm, now, ct);
            await emailTokens.InvalidateOutstandingAsync(user.Id, EmailTokenPurpose.Reset, now, ct);
            outbox.Add(new UserDeletedV1(user.Id, now));
            await uow.SaveChangesAsync(ct);
            return Result.Success();
        }, ct);

        // BR-09 as for a lock: the access token presented here stops working immediately. Other devices' tokens expire within 15 minutes
        // and every endpoint treats a Deleted user as not found.
        if (result.IsSuccess && cmd.Jti is not null && cmd.AccessTokenExpiresAt is { } exp && exp > now)
            await revocation.RevokeAccessTokenAsync(cmd.Jti, exp - now, ct);

        return result;
    }
}
