using BuildingBlocks.Common;

namespace Identity.Domain;

public enum UserStatus { Active, Locked, Deleted }

public enum FailedLoginOutcome { Counted, LockedOut }

/// <summary>User aggregate. All timestamps are UTC and passed in by the caller (TimeProvider), never read from the clock here.</summary>
public sealed class User
{
    private readonly List<UserRole> _roles = [];

    private User() { }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string NormalizedEmail { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string? FullName { get; private set; }
    public string? Phone { get; private set; }
    /// <summary>Id of the avatar in the (future) media service; identity only stores the reference.</summary>
    public Guid? AvatarAssetId { get; private set; }
    public bool EmailConfirmed { get; private set; }
    public UserStatus Status { get; private set; } = UserStatus.Active;
    public int FailedLoginCount { get; private set; }
    public DateTime? LockoutEnd { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token, incremented by the DbContext on every update.</summary>
    public int RowVersion { get; set; }

    public IReadOnlyCollection<UserRole> Roles => _roles;

    /// <summary>BR-01: emails are unique case-insensitively; lookups use the upper-invariant form.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public static User Register(string email, string? fullName, string passwordHash, Role customerRole, DateTime now)
    {
        var user = new User
        {
            Id = Ids.NewGuidV7(),
            Email = email.Trim(),
            NormalizedEmail = NormalizeEmail(email),
            PasswordHash = passwordHash,
            FullName = fullName?.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        user._roles.Add(new UserRole(user.Id, customerRole.Id) { Role = customerRole });
        return user;
    }

    public bool IsDeleted => Status == UserStatus.Deleted;

    /// <summary>True while an admin lock is in place or a brute-force lockout has not expired (BR-03).</summary>
    public bool IsLockedOut(DateTime now) => Status == UserStatus.Locked || (LockoutEnd is { } end && end > now);

    /// <summary>BR-03: after <paramref name="maxFailures"/> consecutive failures the account is locked for <paramref name="lockoutDuration"/>.</summary>
    public FailedLoginOutcome RegisterFailedLogin(DateTime now, int maxFailures, TimeSpan lockoutDuration)
    {
        FailedLoginCount++;
        UpdatedAt = now;
        if (FailedLoginCount < maxFailures) return FailedLoginOutcome.Counted;

        LockoutEnd = now + lockoutDuration;
        FailedLoginCount = 0;
        return FailedLoginOutcome.LockedOut;
    }

    public void RegisterSuccessfulLogin(DateTime now)
    {
        if (FailedLoginCount == 0 && LockoutEnd is null) return;
        FailedLoginCount = 0;
        LockoutEnd = null;
        UpdatedAt = now;
    }

    /// <summary>US-02.1: null arguments leave a field unchanged; <paramref name="clearPhone"/> / <paramref name="clearAvatar"/> remove it.</summary>
    public void UpdateProfile(string? fullName, string? phone, bool clearPhone, Guid? avatarAssetId, bool clearAvatar, DateTime now)
    {
        if (fullName is not null) FullName = fullName.Trim();
        if (clearPhone) Phone = null;
        else if (phone is not null) Phone = phone;
        if (clearAvatar) AvatarAssetId = null;
        else if (avatarAssetId is not null) AvatarAssetId = avatarAssetId;
        UpdatedAt = now;
    }

    /// <summary>
    /// BR-06: soft delete with anonymisation. The row stays (orders and reviews keep a stable user id), but nothing identifying
    /// is left and the password hash is replaced by a value no password can ever match.
    /// </summary>
    public void MarkDeleted(string unusablePasswordHash, DateTime now)
    {
        Status = UserStatus.Deleted;
        Email = $"deleted+{Id}@invalid";
        NormalizedEmail = NormalizeEmail(Email);
        PasswordHash = unusablePasswordHash;
        FullName = null;
        Phone = null;
        AvatarAssetId = null;
        EmailConfirmed = false;
        FailedLoginCount = 0;
        LockoutEnd = null;
        UpdatedAt = now;
    }

    public void ConfirmEmail(DateTime now)
    {
        EmailConfirmed = true;
        UpdatedAt = now;
    }

    public void SetPassword(string passwordHash, DateTime now)
    {
        PasswordHash = passwordHash;
        FailedLoginCount = 0;
        LockoutEnd = null;
        UpdatedAt = now;
    }

    /// <summary>Admin lock (US-01.8). Stays until an admin unlocks.</summary>
    public void Lock(DateTime now)
    {
        Status = UserStatus.Locked;
        UpdatedAt = now;
    }

    public void Unlock(DateTime now)
    {
        if (Status == UserStatus.Locked) Status = UserStatus.Active;
        FailedLoginCount = 0;
        LockoutEnd = null;
        UpdatedAt = now;
    }

    public void SetRoles(IEnumerable<Role> roles, DateTime now)
    {
        var wanted = roles.DistinctBy(r => r.Id).ToList();
        _roles.RemoveAll(ur => wanted.All(r => r.Id != ur.RoleId));
        foreach (var role in wanted.Where(r => _roles.All(ur => ur.RoleId != r.Id)))
            _roles.Add(new UserRole(Id, role.Id) { Role = role });
        UpdatedAt = now;
    }

    public IReadOnlyList<string> RoleNames => _roles.Select(r => r.Role.Name).OrderBy(n => n).ToList();

    public IReadOnlyList<string> Permissions =>
        _roles.SelectMany(r => r.Role.Permissions).Select(p => p.Permission).Distinct().OrderBy(p => p).ToList();
}

public sealed class UserRole(Guid userId, Guid roleId)
{
    public Guid UserId { get; private set; } = userId;
    public Guid RoleId { get; private set; } = roleId;
    public Role Role { get; set; } = null!;
}
