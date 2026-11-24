using BuildingBlocks.Common;
using FluentValidation;
using Identity.Application.Abstractions;
using Identity.Domain;
using MediatR;
using Microsoft.Extensions.Options;

namespace Identity.Application.Auth;

// ---------------------------------------------------------------- Register (US-01.1, BR-01, BR-02, BR-08)

public sealed record RegisterCommand(string Email, string Password, string FullName) : IRequest<Result<RegisterResponse>>;

public sealed record RegisterResponse(Guid UserId);

public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256).EmailAddress();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty().WithErrorCode(IdentityErrors.WeakPasswordCode).MeetsPasswordPolicy();
    }
}

public sealed class RegisterHandler(
    IUserRepository users, IRoleRepository roles, IEmailTokenRepository emailTokens, IPasswordHasher hasher,
    IOutbox outbox, IUnitOfWork uow, AuthEmails emails, IOptions<AuthOptions> options, TimeProvider time)
    : IRequestHandler<RegisterCommand, Result<RegisterResponse>>
{
    public async Task<Result<RegisterResponse>> Handle(RegisterCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var normalized = User.NormalizeEmail(cmd.Email);

        if (await users.EmailExistsAsync(normalized, ct))
            return Result.Failure<RegisterResponse>(IdentityErrors.EmailTaken);

        var customer = await roles.GetRequiredAsync(RoleNames.Customer, ct);
        var user = User.Register(cmd.Email, cmd.FullName, hasher.Hash(cmd.Password), customer, now);
        users.Add(user);

        var token = SecureToken.Generate();
        emailTokens.Add(EmailToken.Create(user.Id, EmailTokenPurpose.Confirm, SecureToken.Hash(token), now,
            TimeSpan.FromHours(options.Value.ConfirmTokenHours)));
        outbox.Add(new UserRegisteredV1(user.Id, user.Email, user.FullName));

        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (UniqueConstraintViolationException)
        {
            // Lost a race with a concurrent registration of the same email.
            return Result.Failure<RegisterResponse>(IdentityErrors.EmailTaken);
        }

        await emails.SendConfirmationAsync(user.Id, user.Email, user.FullName, token, ct);
        return Result.Success(new RegisterResponse(user.Id));
    }
}

// ---------------------------------------------------------------- Login (US-01.2, BR-03, BR-04, BR-07)

public sealed record LoginCommand(string Email, string Password, ClientInfo Client) : IRequest<Result<LoginResponse>>;

public sealed record LoginResponse(string AccessToken, string RefreshToken, int ExpiresIn, UserSummary User);

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class LoginHandler(
    IUserRepository users, IRefreshTokenRepository refreshTokens, IPasswordHasher hasher, IAccessTokenService accessTokens,
    ILoginThrottle throttle, IUnitOfWork uow, IOptions<AuthOptions> options, TimeProvider time)
    : IRequestHandler<LoginCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var opts = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var ip = cmd.Client.Ip;
        var normalized = User.NormalizeEmail(cmd.Email);

        if (ip is not null && await throttle.IsIpBlockedAsync(ip, ct))
            return Fail(IdentityErrors.TooManyAttempts, "throttled");

        var user = await users.GetByNormalizedEmailAsync(normalized, ct);
        if (user is null || user.IsDeleted)
        {
            // Unknown emails behave exactly like real ones (same work, same lockout) so nothing reveals existence (BR-07).
            if (await throttle.IsUnknownEmailLockedAsync(normalized, ct))
                return Fail(IdentityErrors.LockedOut, "locked_out");

            hasher.BurnVerification(cmd.Password);
            await throttle.RecordUnknownEmailFailureAsync(normalized, ct);
            if (ip is not null) await throttle.RecordIpFailureAsync(ip, ct);
            return Fail(IdentityErrors.InvalidCredentials, "invalid_credentials");
        }

        // A locked account answers "locked" even for the right password until the lock ends (AC-3).
        if (user.IsLockedOut(now))
            return Fail(IdentityErrors.LockedOut, "locked_out");

        var verification = hasher.Verify(user.PasswordHash, cmd.Password);
        if (verification == PasswordVerification.Failed)
        {
            user.RegisterFailedLogin(now, opts.MaxFailedLogins, opts.Lockout);
            try
            {
                await uow.SaveChangesAsync(ct);
            }
            catch (ConcurrentUpdateException)
            {
                // A parallel attempt updated the counter first; the failure is still reported below.
            }

            if (ip is not null) await throttle.RecordIpFailureAsync(ip, ct);
            return Fail(IdentityErrors.InvalidCredentials, "invalid_credentials");
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
            user.SetPassword(hasher.Hash(cmd.Password), now);
        user.RegisterSuccessfulLogin(now);

        var refreshToken = SecureToken.Generate();
        refreshTokens.Add(RefreshToken.Issue(user.Id, SecureToken.Hash(refreshToken), now, ip, cmd.Client.UserAgent));
        var access = accessTokens.Create(user, now);
        await uow.SaveChangesAsync(ct);

        IdentityMetrics.Login("success");
        return Result.Success(new LoginResponse(
            access.Value, refreshToken, (int)(access.ExpiresAt - now).TotalSeconds,
            new UserSummary(user.Id, user.Email, user.FullName, user.RoleNames)));
    }

    private static Result<LoginResponse> Fail(Error error, string outcome)
    {
        IdentityMetrics.Login(outcome);
        return Result.Failure<LoginResponse>(error);
    }
}
