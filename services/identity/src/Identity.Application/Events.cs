using Identity.Application.Abstractions;

namespace Identity.Application;

/// <summary>
/// Integration events published to <c>identity.user-events</c> (key = userId). Until Kafka arrives (Phase 2) a relay does not exist,
/// so the rows simply accumulate in <c>outbox_messages</c>. Schemas live in contracts/events.
/// </summary>
public abstract record IdentityEvent(Guid UserId) : IntegrationEvent
{
    public override string Topic => "identity.user-events";
    public override string AggregateType => "User";
    public override string AggregateId => UserId.ToString();
}

public sealed record UserRegisteredV1(Guid UserId, string Email, string? FullName) : IdentityEvent(UserId)
{
    public const string EventType = "identity.user-registered.v1";
    public override string Type => EventType;
    public override object Data => new { userId = UserId, email = Email, fullName = FullName };
}

/// <summary>Carries the one-time reset token for the notification service (spec section 5). Never log this payload.</summary>
public sealed record PasswordResetRequestedV1(Guid UserId, string Email, string ResetToken, DateTime ExpiresAt) : IdentityEvent(UserId)
{
    public const string EventType = "identity.password-reset-requested.v1";
    public override string Type => EventType;
    public override object Data => new { userId = UserId, email = Email, resetToken = ResetToken, expiresAt = ExpiresAt };
}

public sealed record UserLockedV1(Guid UserId, Guid LockedBy, DateTime LockedAt) : IdentityEvent(UserId)
{
    public const string EventType = "identity.user-locked.v1";
    public override string Type => EventType;
    public override object Data => new { userId = UserId, lockedBy = LockedBy, lockedAt = LockedAt };
}

/// <summary>BR-06: other services anonymise their copies (reviews show "Deleted user"). Carries no PII, only the id.</summary>
public sealed record UserDeletedV1(Guid UserId, DateTime DeletedAt) : IdentityEvent(UserId)
{
    public const string EventType = "identity.user-deleted.v1";
    public override string Type => EventType;
    public override object Data => new { userId = UserId, deletedAt = DeletedAt };
}
