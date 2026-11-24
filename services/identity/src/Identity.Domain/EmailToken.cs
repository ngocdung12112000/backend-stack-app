using BuildingBlocks.Common;

namespace Identity.Domain;

public enum EmailTokenPurpose { Confirm, Reset }

/// <summary>Single-use, hashed, expiring token for email confirmation or password reset.</summary>
public sealed class EmailToken
{
    private EmailToken() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public EmailTokenPurpose Purpose { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? UsedAt { get; private set; }

    public static EmailToken Create(Guid userId, EmailTokenPurpose purpose, string tokenHash, DateTime now, TimeSpan ttl) => new()
    {
        Id = Ids.NewGuidV7(),
        UserId = userId,
        Purpose = purpose,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now + ttl,
    };

    public bool IsUsable(DateTime now) => UsedAt is null && ExpiresAt > now;

    public void MarkUsed(DateTime now) => UsedAt ??= now;
}
