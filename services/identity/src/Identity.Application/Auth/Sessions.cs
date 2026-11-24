using BuildingBlocks.Common;
using FluentValidation;
using Identity.Application.Abstractions;
using Identity.Domain;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Application.Auth;

// ---------------------------------------------------------------- Refresh (BR-05, BR-06, BR-09)

public sealed record RefreshCommand(string RefreshToken, ClientInfo Client) : IRequest<Result<TokenPairResponse>>;

public sealed record TokenPairResponse(string AccessToken, string RefreshToken, int ExpiresIn);

public sealed class RefreshValidator : AbstractValidator<RefreshCommand>
{
    public RefreshValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(200);
}

public sealed class RefreshHandler(
    IRefreshTokenRepository refreshTokens, IUserRepository users, IAccessTokenService accessTokens,
    IUnitOfWork uow, TimeProvider time, ILogger<RefreshHandler> logger)
    : IRequestHandler<RefreshCommand, Result<TokenPairResponse>>
{
    public async Task<Result<TokenPairResponse>> Handle(RefreshCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var current = await refreshTokens.GetByHashAsync(SecureToken.Hash(cmd.RefreshToken), ct);
        if (current is null)
            return Fail(IdentityErrors.RefreshInvalid, "invalid");

        // BR-06: a token that was already exchanged must never be presented again. Treat it as theft and kill the whole family.
        if (current.WasRotated)
        {
            await refreshTokens.RevokeFamilyAsync(current.FamilyId, now, ct);
            await uow.SaveChangesAsync(ct);
            logger.LogWarning("Refresh token reuse detected: family {FamilyId} of user {UserId} revoked", current.FamilyId, current.UserId);
            return Fail(IdentityErrors.RefreshReused, "reused");
        }

        if (!current.IsActive(now))
            return Fail(IdentityErrors.RefreshInvalid, "invalid");

        // BR-09: locked or deleted users cannot refresh.
        var user = await users.GetByIdAsync(current.UserId, ct);
        if (user is null || user.Status != UserStatus.Active || user.IsLockedOut(now))
        {
            await refreshTokens.RevokeFamilyAsync(current.FamilyId, now, ct);
            await uow.SaveChangesAsync(ct);
            return Fail(IdentityErrors.RefreshInvalid, "invalid");
        }

        var newToken = SecureToken.Generate();
        refreshTokens.Add(current.Rotate(SecureToken.Hash(newToken), now, cmd.Client.Ip, cmd.Client.UserAgent));
        var access = accessTokens.Create(user, now);

        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (ConcurrentUpdateException)
        {
            // Two requests rotated the same token at once; only one may win.
            return Fail(IdentityErrors.RefreshInvalid, "invalid");
        }

        IdentityMetrics.Refresh("success");
        return Result.Success(new TokenPairResponse(access.Value, newToken, (int)(access.ExpiresAt - now).TotalSeconds));
    }

    private static Result<TokenPairResponse> Fail(Error error, string outcome)
    {
        IdentityMetrics.Refresh(outcome);
        return Result.Failure<TokenPairResponse>(error);
    }
}

// ---------------------------------------------------------------- Logout (US-01.5, AC-5)

/// <param name="Jti">The caller's access-token id; blacklisted until the token would have expired.</param>
public sealed record LogoutCommand(Guid UserId, string? RefreshToken, bool AllDevices, string? Jti, DateTime? AccessTokenExpiresAt)
    : IRequest<Result>;

public sealed class LogoutHandler(
    IRefreshTokenRepository refreshTokens, ITokenRevocation revocation, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<LogoutCommand, Result>
{
    public async Task<Result> Handle(LogoutCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;

        if (cmd.Jti is not null && cmd.AccessTokenExpiresAt is { } exp)
            await revocation.RevokeAccessTokenAsync(cmd.Jti, exp - now, ct);

        if (cmd.AllDevices)
        {
            await refreshTokens.RevokeAllForUserAsync(cmd.UserId, now, ct);
        }
        else if (!string.IsNullOrEmpty(cmd.RefreshToken))
        {
            var token = await refreshTokens.GetByHashAsync(SecureToken.Hash(cmd.RefreshToken), ct);
            // Only the owner's tokens count; anything else is ignored so logout stays idempotent and leaks nothing.
            if (token is not null && token.UserId == cmd.UserId)
                await refreshTokens.RevokeFamilyAsync(token.FamilyId, now, ct);
        }

        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ---------------------------------------------------------------- Sessions (spec section 4)

public sealed record SessionDto(Guid FamilyId, string? UserAgent, string? Ip, DateTime StartedAt, DateTime LastUsedAt, DateTime ExpiresAt);

public sealed record ListSessionsQuery(Guid UserId) : IRequest<Result<IReadOnlyList<SessionDto>>>;

public sealed class ListSessionsHandler(IRefreshTokenRepository refreshTokens, TimeProvider time)
    : IRequestHandler<ListSessionsQuery, Result<IReadOnlyList<SessionDto>>>
{
    public async Task<Result<IReadOnlyList<SessionDto>>> Handle(ListSessionsQuery q, CancellationToken ct)
    {
        var tokens = await refreshTokens.ListActiveSessionsAsync(q.UserId, time.GetUtcNow().UtcDateTime, ct);
        IReadOnlyList<SessionDto> sessions = tokens
            .Select(t => new SessionDto(t.FamilyId, t.UserAgent, t.CreatedIp, t.FamilyStartedAt, t.CreatedAt, t.ExpiresAt))
            .OrderByDescending(s => s.LastUsedAt)
            .ToList();
        return Result.Success(sessions);
    }
}

public sealed record RevokeSessionCommand(Guid UserId, Guid FamilyId) : IRequest<Result>;

public sealed class RevokeSessionHandler(IRefreshTokenRepository refreshTokens, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<RevokeSessionCommand, Result>
{
    public async Task<Result> Handle(RevokeSessionCommand cmd, CancellationToken ct)
    {
        // Not the caller's session -> 404, never reveal that the family exists.
        if (!await refreshTokens.RevokeUserFamilyAsync(cmd.UserId, cmd.FamilyId, time.GetUtcNow().UtcDateTime, ct))
            return Result.Failure(IdentityErrors.SessionNotFound);

        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
