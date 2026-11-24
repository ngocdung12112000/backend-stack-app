using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Common;
using FluentValidation;
using MediatR;

namespace Identity.Application;

/// <summary>Tunable rules from F01 section 2. Bound from the "Auth" configuration section and validated on start.</summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    public int AccessTokenMinutes { get; set; } = 15;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int IpAttemptLimit { get; set; } = 20;
    public int IpWindowMinutes { get; set; } = 15;
    public int ConfirmTokenHours { get; set; } = 24;
    public int ResetTokenMinutes { get; set; } = 30;

    /// <summary>Base URL of the storefront; confirmation and reset links in emails point here.</summary>
    public string FrontendBaseUrl { get; set; } = "http://localhost:3000";

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);
    public TimeSpan Lockout => TimeSpan.FromMinutes(LockoutMinutes);
}

public sealed record ClientInfo(string? Ip, string? UserAgent);

public static class IdentityErrors
{
    public const string WeakPasswordCode = "IDENTITY_WEAK_PASSWORD";

    public static readonly Error EmailTaken = Error.Conflict("IDENTITY_EMAIL_TAKEN", "An account with this email already exists.");
    public static readonly Error InvalidCredentials = Error.Unauthorized("IDENTITY_INVALID_CREDENTIALS", "Email or password is incorrect.");
    public static readonly Error LockedOut = Error.Locked("IDENTITY_LOCKED_OUT", "This account is temporarily locked. Try again later.");
    public static readonly Error TooManyAttempts = Error.TooManyRequests("IDENTITY_TOO_MANY_ATTEMPTS", "Too many failed attempts. Try again later.");
    public static readonly Error TokenInvalid = Error.Validation("IDENTITY_TOKEN_INVALID", "The token is invalid or has expired.");
    public static readonly Error RefreshReused = Error.Unauthorized("IDENTITY_REFRESH_REUSED", "The refresh token was already used. All sessions of this login were revoked.");
    public static readonly Error RefreshInvalid = Error.Unauthorized("IDENTITY_REFRESH_INVALID", "The refresh token is invalid or has expired.");
    public static readonly Error InvalidCurrentPassword = Error.Validation("IDENTITY_INVALID_CURRENT_PASSWORD", "The current password is incorrect.");
    public static readonly Error UserNotFound = Error.NotFound("IDENTITY_USER_NOT_FOUND", "User not found.");
    public static readonly Error SessionNotFound = Error.NotFound("IDENTITY_SESSION_NOT_FOUND", "Session not found.");
    public static readonly Error CannotLockSelf = Error.Conflict("IDENTITY_CANNOT_LOCK_SELF", "You cannot lock your own account.");
    public static Error UnknownRole(string role) => Error.Validation("IDENTITY_UNKNOWN_ROLE", $"Unknown role '{role}'.");
}

/// <summary>Business metrics named in F01 section 7. The OpenTelemetry Prometheus exporter appends "_total" to counters.</summary>
public static class IdentityMetrics
{
    public const string MeterName = "StackShop.Identity";
    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Logins = Meter.CreateCounter<long>("stackshop_auth_logins", description: "Login attempts by outcome");
    private static readonly Counter<long> Refreshes = Meter.CreateCounter<long>("stackshop_auth_refresh", description: "Refresh attempts by outcome");

    public static void Login(string outcome) => Logins.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    public static void Refresh(string outcome) => Refreshes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}

/// <summary>Opaque random tokens (refresh, email confirm, password reset). Only the SHA-256 hash is ever stored.</summary>
public static class SecureToken
{
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class PasswordRules
{
    /// <summary>BR-02: at least 8 characters (max 128), at least one letter and one digit.</summary>
    public static IRuleBuilderOptions<T, string> MeetsPasswordPolicy<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(p => p is { Length: >= 8 and <= 128 } && p.Any(char.IsLetter) && p.Any(char.IsDigit))
            .WithErrorCode(IdentityErrors.WeakPasswordCode)
            .WithMessage("Password must be 8-128 characters and contain at least one letter and one digit.");
}

/// <summary>Runs FluentValidation before every handler; failures surface as a 400 ProblemDetails with the field errors.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);
            var failures = (await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct))))
                .SelectMany(r => r.Errors)
                .Where(f => f is not null)
                .ToList();
            if (failures.Count > 0) throw new ValidationException(failures);
        }

        return await next();
    }
}

public sealed record UserSummary(Guid Id, string Email, string? FullName, IReadOnlyList<string> Roles);

public sealed record UserProfile(
    Guid Id, string Email, string? FullName, string? Phone, bool EmailConfirmed, string Status,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions, DateTime CreatedAt);
