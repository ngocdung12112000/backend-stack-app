using BuildingBlocks.Common;
using Identity.Domain;

namespace Identity.Application.Abstractions;

public interface IUnitOfWork
{
    /// <exception cref="UniqueConstraintViolationException">A unique index (e.g. users.normalized_email) rejected the write.</exception>
    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class UniqueConstraintViolationException(Exception inner)
    : Exception("A unique constraint was violated.", inner);

/// <summary>Raised when optimistic concurrency detects that another request changed the same row first.</summary>
public sealed class ConcurrentUpdateException(Exception inner)
    : Exception("The record was modified by another request.", inner);

public interface IUserRepository
{
    /// <summary>Loads the user with roles and role permissions.</summary>
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct);
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct);
    void Add(User user);
    Task<PagedResult<User>> ListAsync(string? search, string? role, UserStatus? status, int page, int pageSize, CancellationToken ct);
}

public interface IRoleRepository
{
    Task<Role> GetRequiredAsync(string name, CancellationToken ct);
    Task<IReadOnlyList<Role>> GetByNamesAsync(IEnumerable<string> names, CancellationToken ct);
}

public interface IRefreshTokenRepository
{
    void Add(RefreshToken token);
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);
    /// <summary>Revokes every still-active token of the family (tracked; persisted by <see cref="IUnitOfWork"/>).</summary>
    Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct);
    /// <summary>Revokes every active token of the user and returns how many were revoked.</summary>
    Task<int> RevokeAllForUserAsync(Guid userId, DateTime now, CancellationToken ct);
    /// <summary>Revokes the family only when it belongs to the user. Returns false when there is no such active session.</summary>
    Task<bool> RevokeUserFamilyAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct);
    /// <summary>One active token per live session (family) of the user.</summary>
    Task<IReadOnlyList<RefreshToken>> ListActiveSessionsAsync(Guid userId, DateTime now, CancellationToken ct);
}

public interface IEmailTokenRepository
{
    void Add(EmailToken token);
    Task<EmailToken?> FindUsableAsync(Guid userId, EmailTokenPurpose purpose, string tokenHash, DateTime now, CancellationToken ct);
    /// <summary>Marks unused tokens of that purpose as used so only the newest link works.</summary>
    Task InvalidateOutstandingAsync(Guid userId, EmailTokenPurpose purpose, DateTime now, CancellationToken ct);
}

/// <summary>Transactional outbox: the row is written in the same transaction as the state change (docs/04 section 4).</summary>
public interface IOutbox
{
    void Add(IntegrationEvent integrationEvent);
}

public interface IPasswordHasher
{
    string Hash(string password);
    PasswordVerification Verify(string hash, string password);
    /// <summary>Burns the same CPU as a real verification; used for unknown emails so response time does not reveal them.</summary>
    void BurnVerification(string password);
}

public enum PasswordVerification { Failed, Success, SuccessRehashNeeded }

public sealed record AccessToken(string Value, string Jti, DateTime ExpiresAt);

public interface IAccessTokenService
{
    AccessToken Create(User user, DateTime now);
}

/// <summary>Access-token blacklist (Redis <c>auth:revoked-jti:{jti}</c>), checked by the gateway and by identity itself.</summary>
public interface ITokenRevocation
{
    Task RevokeAccessTokenAsync(string jti, TimeSpan remainingLifetime, CancellationToken ct);
}

/// <summary>Brute-force counters kept in Redis (BR-03).</summary>
public interface ILoginThrottle
{
    Task<bool> IsIpBlockedAsync(string ip, CancellationToken ct);
    Task RecordIpFailureAsync(string ip, CancellationToken ct);
    /// <summary>Unknown emails get the same lockout behaviour as real ones so responses never reveal existence (BR-07).</summary>
    Task<bool> IsUnknownEmailLockedAsync(string normalizedEmail, CancellationToken ct);
    Task RecordUnknownEmailFailureAsync(string normalizedEmail, CancellationToken ct);
}

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>Fire-and-forget delivery: enqueueing must not depend on (or reveal the latency of) the SMTP server.</summary>
public interface IEmailSender
{
    ValueTask EnqueueAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>Base type of integration events written to the outbox.</summary>
public abstract record IntegrationEvent
{
    public abstract string Type { get; }
    public abstract string Topic { get; }
    public abstract string AggregateType { get; }
    public abstract string AggregateId { get; }
    /// <summary>The event-specific <c>data</c> object of the envelope.</summary>
    public abstract object Data { get; }
}
