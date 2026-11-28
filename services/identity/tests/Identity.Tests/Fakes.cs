using BuildingBlocks.Common;
using Identity.Application;
using Identity.Application.Abstractions;
using Identity.Application.Auth;
using Identity.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Identity.Tests;

/// <summary>In-memory implementation of every persistence port, so handlers run against real domain objects without a database.</summary>
public sealed class FakeStore : IUserRepository, IRoleRepository, IRefreshTokenRepository, IEmailTokenRepository, IAddressRepository, IOutbox, IUnitOfWork
{
    public List<User> Users { get; } = [];
    public List<RefreshToken> RefreshTokens { get; } = [];
    public List<Address> Addresses { get; } = [];
    public List<EmailToken> EmailTokens { get; } = [];
    public List<IntegrationEvent> Events { get; } = [];

    public Role Customer { get; } = new(IdentitySeed.CustomerRoleId, RoleNames.Customer);
    public Role Admin { get; } = new(IdentitySeed.AdminRoleId, RoleNames.Admin);

    // IUnitOfWork
    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct = default) => work();

    // IAddressRepository
    public Task<IReadOnlyList<Address>> ListAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Address>>(Addresses.Where(a => a.UserId == userId).ToList());
    public Task<AddressBook?> LoadForUpdateAsync(Guid userId, CancellationToken ct) =>
        Task.FromResult(Users.Any(u => u.Id == userId && !u.IsDeleted)
            ? new AddressBook(userId, Addresses.Where(a => a.UserId == userId).ToList())
            : null);
    public void Add(Address address) => Addresses.Add(address);
    public void Remove(Address address) => Addresses.Remove(address);

    // IUserRepository
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Users.FirstOrDefault(u => u.Id == id));
    public Task<User?> GetByNormalizedEmailAsync(string e, CancellationToken ct) => Task.FromResult(Users.FirstOrDefault(u => u.NormalizedEmail == e));
    public Task<bool> EmailExistsAsync(string e, CancellationToken ct) => Task.FromResult(Users.Any(u => u.NormalizedEmail == e));
    public void Add(User user) => Users.Add(user);
    public Task<PagedResult<User>> ListAsync(string? s, string? r, UserStatus? st, int page, int size, CancellationToken ct) =>
        Task.FromResult(new PagedResult<User>(Users, page, size, Users.Count));

    // IRoleRepository
    public Task<Role> GetRequiredAsync(string name, CancellationToken ct) =>
        Task.FromResult(name == RoleNames.Admin ? Admin : Customer);
    public Task<IReadOnlyList<Role>> GetByNamesAsync(IEnumerable<string> names, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Role>>(new[] { Customer, Admin }.Where(r => names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList());

    // IRefreshTokenRepository
    public void Add(RefreshToken token) => RefreshTokens.Add(token);
    public Task<RefreshToken?> GetByHashAsync(string hash, CancellationToken ct) => Task.FromResult(RefreshTokens.FirstOrDefault(t => t.TokenHash == hash));
    public Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct)
    {
        RefreshTokens.Where(t => t.FamilyId == familyId).ToList().ForEach(t => t.Revoke(now));
        return Task.CompletedTask;
    }
    public Task<int> RevokeAllForUserAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var tokens = RefreshTokens.Where(t => t.UserId == userId && !t.IsRevoked).ToList();
        tokens.ForEach(t => t.Revoke(now));
        return Task.FromResult(tokens.Count);
    }
    public Task<bool> RevokeUserFamilyAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct)
    {
        var tokens = RefreshTokens.Where(t => t.UserId == userId && t.FamilyId == familyId && t.IsActive(now)).ToList();
        tokens.ForEach(t => t.Revoke(now));
        return Task.FromResult(tokens.Count > 0);
    }
    public Task<IReadOnlyList<RefreshToken>> ListActiveSessionsAsync(Guid userId, DateTime now, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RefreshToken>>(RefreshTokens.Where(t => t.UserId == userId && t.IsActive(now)).ToList());

    // IEmailTokenRepository
    public void Add(EmailToken token) => EmailTokens.Add(token);
    public Task<EmailToken?> FindUsableAsync(Guid userId, EmailTokenPurpose p, string hash, DateTime now, CancellationToken ct) =>
        Task.FromResult(EmailTokens.FirstOrDefault(t => t.UserId == userId && t.Purpose == p && t.TokenHash == hash && t.IsUsable(now)));
    public Task InvalidateOutstandingAsync(Guid userId, EmailTokenPurpose p, DateTime now, CancellationToken ct)
    {
        EmailTokens.Where(t => t.UserId == userId && t.Purpose == p).ToList().ForEach(t => t.MarkUsed(now));
        return Task.CompletedTask;
    }

    // IOutbox
    public void Add(IntegrationEvent e) => Events.Add(e);
}

public sealed class FakeHasher : Application.Abstractions.IPasswordHasher
{
    public string Hash(string password) => "hash:" + password;
    public PasswordVerification Verify(string hash, string password) =>
        hash == "hash:" + password ? PasswordVerification.Success : PasswordVerification.Failed;
    public void BurnVerification(string password) { }
}

public sealed class FakeAccessTokens : IAccessTokenService
{
    public AccessToken Create(User user, DateTime now) => new($"access-{Guid.NewGuid():N}", Guid.NewGuid().ToString(), now.AddMinutes(15));
}

public sealed class FakeThrottle : ILoginThrottle
{
    public Dictionary<string, int> Ip { get; } = [];
    public Dictionary<string, int> Unknown { get; } = [];
    public Task<bool> IsIpBlockedAsync(string ip, CancellationToken ct) => Task.FromResult(Ip.GetValueOrDefault(ip) >= 20);
    public Task RecordIpFailureAsync(string ip, CancellationToken ct) { Ip[ip] = Ip.GetValueOrDefault(ip) + 1; return Task.CompletedTask; }
    public Task<bool> IsUnknownEmailLockedAsync(string e, CancellationToken ct) => Task.FromResult(Unknown.GetValueOrDefault(e) >= 5);
    public Task RecordUnknownEmailFailureAsync(string e, CancellationToken ct) { Unknown[e] = Unknown.GetValueOrDefault(e) + 1; return Task.CompletedTask; }
}

public sealed class FakeEmailSender : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];
    public ValueTask EnqueueAsync(EmailMessage message, CancellationToken ct = default) { Sent.Add(message); return ValueTask.CompletedTask; }
}

public sealed class FakeRevocation : ITokenRevocation
{
    public Dictionary<string, TimeSpan> Revoked { get; } = [];
    public Task RevokeAccessTokenAsync(string jti, TimeSpan ttl, CancellationToken ct) { Revoked[jti] = ttl; return Task.CompletedTask; }
}

/// <summary>Wires the handlers the same way the DI container does, with fakes underneath.</summary>
public sealed class Harness
{
    public FakeStore Store { get; } = new();
    public FakeHasher Hasher { get; } = new();
    public FakeThrottle Throttle { get; } = new();
    public FakeEmailSender Email { get; } = new();
    public FakeRevocation Revocation { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));
    public IOptions<AuthOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new AuthOptions());
    public ClientInfo Client { get; } = new("10.0.0.1", "xunit");

    private readonly FakeAccessTokens _access = new();

    public RegisterHandler Register => new(Store, Store, Store, Hasher, Store, Store, Emails, Options, Time);
    public LoginHandler Login => new(Store, Store, Hasher, _access, Throttle, Store, Options, Time);
    public RefreshHandler Refresh => new(Store, Store, _access, Store, Time, NullLogger<RefreshHandler>.Instance);
    public LogoutHandler Logout => new(Store, Revocation, Store, Time);
    public ForgotPasswordHandler Forgot => new(Store, Store, Store, Store, Emails, Options, Time);
    public ResetPasswordHandler Reset => new(Store, Store, Store, Hasher, Store, Time);
    public ConfirmEmailHandler Confirm => new(Store, Store, Store, Time);
    public Application.Users.LockUserHandler Lock => new(Store, Store, Store, Store, Time);
    public AuthEmails Emails => new(Options, Email);

    public const string Password = "Passw0rd!";

    public async Task<User> RegisteredUser(string email = "ann@example.com")
    {
        var result = await Register.Handle(new RegisterCommand(email, Password, "Ann Example"), default);
        result.IsSuccess.Should().BeTrue();
        return Store.Users.Single(u => u.Id == result.Value.UserId);
    }

    public async Task<LoginResponse> LoggedIn(string email = "ann@example.com")
    {
        var result = await Login.Handle(new LoginCommand(email, Password, Client), default);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }
}
