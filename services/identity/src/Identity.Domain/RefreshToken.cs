using BuildingBlocks.Common;

namespace Identity.Domain;

/// <summary>
/// One link in a rotation chain (a "family" = one login session). Only the SHA-256 hash of the opaque token is stored.
/// BR-05: sliding 7-day TTL with rotation, absolute cap of 30 days per family.
/// BR-06: presenting a token that was already rotated is reuse and revokes the whole family.
/// </summary>
public sealed class RefreshToken
{
    public static readonly TimeSpan SlidingTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan AbsoluteCap = TimeSpan.FromDays(30);

    private RefreshToken() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public Guid FamilyId { get; private set; }
    public DateTime FamilyStartedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public Guid? ReplacedById { get; private set; }
    public string? CreatedIp { get; private set; }
    public string? UserAgent { get; private set; }

    /// <summary>Starts a new family (login).</summary>
    public static RefreshToken Issue(Guid userId, string tokenHash, DateTime now, string? ip, string? userAgent) =>
        Create(userId, tokenHash, Ids.NewGuidV7(), now, now, ip, userAgent);

    private static RefreshToken Create(Guid userId, string tokenHash, Guid familyId, DateTime familyStartedAt, DateTime now, string? ip, string? ua)
    {
        var sliding = now + SlidingTtl;
        var cap = familyStartedAt + AbsoluteCap;
        return new RefreshToken
        {
            Id = Ids.NewGuidV7(),
            UserId = userId,
            TokenHash = tokenHash,
            FamilyId = familyId,
            FamilyStartedAt = familyStartedAt,
            CreatedAt = now,
            ExpiresAt = sliding < cap ? sliding : cap,
            CreatedIp = Truncate(ip, 45),
            UserAgent = Truncate(ua, 300),
        };
    }

    public bool IsExpired(DateTime now) => ExpiresAt <= now;
    public bool IsRevoked => RevokedAt is not null;
    public bool IsActive(DateTime now) => !IsRevoked && !IsExpired(now);

    /// <summary>True when this token was already exchanged for a newer one, so presenting it again is a reuse attempt.</summary>
    public bool WasRotated => ReplacedById is not null;

    /// <summary>Exchanges this token for its successor in the same family and revokes this one.</summary>
    public RefreshToken Rotate(string newTokenHash, DateTime now, string? ip, string? userAgent)
    {
        if (!IsActive(now))
            throw new InvalidOperationException("Only an active refresh token can be rotated.");

        var next = Create(UserId, newTokenHash, FamilyId, FamilyStartedAt, now, ip, userAgent);
        RevokedAt = now;
        LastUsedAt = now;
        ReplacedById = next.Id;
        return next;
    }

    public void Revoke(DateTime now) => RevokedAt ??= now;

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
