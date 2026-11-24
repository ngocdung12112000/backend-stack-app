using BuildingBlocks.Common;
using FluentValidation;
using Identity.Application.Abstractions;
using Identity.Domain;
using MediatR;
using Microsoft.Extensions.Options;

namespace Identity.Application.Auth;

// ---------------------------------------------------------------- Confirm email (US-01.3)

public sealed record ConfirmEmailCommand(Guid UserId, string Token) : IRequest<Result>;

public sealed class ConfirmEmailValidator : AbstractValidator<ConfirmEmailCommand>
{
    public ConfirmEmailValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
    }
}

public sealed class ConfirmEmailHandler(IUserRepository users, IEmailTokenRepository emailTokens, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<ConfirmEmailCommand, Result>
{
    public async Task<Result> Handle(ConfirmEmailCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var token = await emailTokens.FindUsableAsync(cmd.UserId, EmailTokenPurpose.Confirm, SecureToken.Hash(cmd.Token), now, ct);
        var user = token is null ? null : await users.GetByIdAsync(cmd.UserId, ct);
        if (token is null || user is null)
            return Result.Failure(IdentityErrors.TokenInvalid);

        token.MarkUsed(now);
        user.ConfirmEmail(now);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record ResendConfirmationCommand(Guid UserId) : IRequest<Result>;

public sealed class ResendConfirmationHandler(
    IUserRepository users, IEmailTokenRepository emailTokens, IUnitOfWork uow, AuthEmails emails,
    IOptions<AuthOptions> options, TimeProvider time)
    : IRequestHandler<ResendConfirmationCommand, Result>
{
    public async Task<Result> Handle(ResendConfirmationCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var user = await users.GetByIdAsync(cmd.UserId, ct);
        if (user is null) return Result.Failure(IdentityErrors.UserNotFound);
        if (user.EmailConfirmed) return Result.Success();

        await emailTokens.InvalidateOutstandingAsync(user.Id, EmailTokenPurpose.Confirm, now, ct);
        var token = SecureToken.Generate();
        emailTokens.Add(EmailToken.Create(user.Id, EmailTokenPurpose.Confirm, SecureToken.Hash(token), now,
            TimeSpan.FromHours(options.Value.ConfirmTokenHours)));
        await uow.SaveChangesAsync(ct);

        await emails.SendConfirmationAsync(user.Id, user.Email, user.FullName, token, ct);
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Forgot / reset password (US-01.4, BR-07)

public sealed record ForgotPasswordCommand(string Email) : IRequest<Result>;

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator() => RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
}

/// <summary>Always succeeds: the response is identical whether or not the account exists (AC-7).</summary>
public sealed class ForgotPasswordHandler(
    IUserRepository users, IEmailTokenRepository emailTokens, IOutbox outbox, IUnitOfWork uow, AuthEmails emails,
    IOptions<AuthOptions> options, TimeProvider time)
    : IRequestHandler<ForgotPasswordCommand, Result>
{
    public async Task<Result> Handle(ForgotPasswordCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var user = await users.GetByNormalizedEmailAsync(User.NormalizeEmail(cmd.Email), ct);
        if (user is null || user.Status != UserStatus.Active)
            return Result.Success();

        await emailTokens.InvalidateOutstandingAsync(user.Id, EmailTokenPurpose.Reset, now, ct);
        var token = SecureToken.Generate();
        var ttl = TimeSpan.FromMinutes(options.Value.ResetTokenMinutes);
        emailTokens.Add(EmailToken.Create(user.Id, EmailTokenPurpose.Reset, SecureToken.Hash(token), now, ttl));
        outbox.Add(new PasswordResetRequestedV1(user.Id, user.Email, token, now + ttl));
        await uow.SaveChangesAsync(ct);

        await emails.SendPasswordResetAsync(user.Email, token, ct);
        return Result.Success();
    }
}

public sealed record ResetPasswordCommand(string Email, string Token, string NewPassword) : IRequest<Result>;

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NewPassword).NotEmpty().WithErrorCode(IdentityErrors.WeakPasswordCode).MeetsPasswordPolicy();
    }
}

public sealed class ResetPasswordHandler(
    IUserRepository users, IEmailTokenRepository emailTokens, IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<ResetPasswordCommand, Result>
{
    public async Task<Result> Handle(ResetPasswordCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var user = await users.GetByNormalizedEmailAsync(User.NormalizeEmail(cmd.Email), ct);
        var token = user is null
            ? null
            : await emailTokens.FindUsableAsync(user.Id, EmailTokenPurpose.Reset, SecureToken.Hash(cmd.Token), now, ct);
        if (user is null || token is null)
            return Result.Failure(IdentityErrors.TokenInvalid);

        token.MarkUsed(now);
        user.SetPassword(hasher.Hash(cmd.NewPassword), now);
        await refreshTokens.RevokeAllForUserAsync(user.Id, now, ct); // every session must log in again
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Change password (US-01.6)

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword) : IRequest<Result>;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(128);
        RuleFor(x => x.NewPassword).NotEmpty().WithErrorCode(IdentityErrors.WeakPasswordCode).MeetsPasswordPolicy();
    }
}

public sealed class ChangePasswordHandler(IUserRepository users, IPasswordHasher hasher, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<ChangePasswordCommand, Result>
{
    public async Task<Result> Handle(ChangePasswordCommand cmd, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(cmd.UserId, ct);
        if (user is null) return Result.Failure(IdentityErrors.UserNotFound);

        // 400, not 401: the caller is authenticated, and a 401 would make the client think its session died.
        if (hasher.Verify(user.PasswordHash, cmd.CurrentPassword) == PasswordVerification.Failed)
            return Result.Failure(IdentityErrors.InvalidCurrentPassword);

        user.SetPassword(hasher.Hash(cmd.NewPassword), time.GetUtcNow().UtcDateTime);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
