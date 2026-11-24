using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks.Common;
using Identity.Application.Abstractions;
using Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Infrastructure.Persistence;

internal sealed class UserRepository(IdentityDb db) : IUserRepository
{
    private IQueryable<User> WithRoles() =>
        db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).ThenInclude(r => r.Permissions).AsSplitQuery();

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) => WithRoles().FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct) =>
        WithRoles().FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, ct);

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct);

    public void Add(User user) => db.Users.Add(user);

    public async Task<PagedResult<User>> ListAsync(string? search, string? role, UserStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var filter = db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filter = filter.Where(u => u.Email.Contains(term) || (u.FullName != null && u.FullName.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(role)) filter = filter.Where(u => u.Roles.Any(r => r.Role.Name == role));
        if (status is { } s) filter = filter.Where(u => u.Status == s);

        var total = await filter.LongCountAsync(ct);
        var ids = await filter.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(u => u.Id).ToListAsync(ct);

        var items = await WithRoles().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        var ordered = ids.Select(id => items.First(u => u.Id == id)).ToList();
        return new PagedResult<User>(ordered, page, pageSize, total);
    }
}

internal sealed class RoleRepository(IdentityDb db) : IRoleRepository
{
    public async Task<Role> GetRequiredAsync(string name, CancellationToken ct) =>
        await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Name == name, ct)
        ?? throw new InvalidOperationException($"Role '{name}' is missing; run the database migrations.");

    public async Task<IReadOnlyList<Role>> GetByNamesAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var wanted = names.Select(n => n.Trim()).ToList();
        return await db.Roles.Include(r => r.Permissions).Where(r => wanted.Contains(r.Name)).ToListAsync(ct);
    }
}

internal sealed class RefreshTokenRepository(IdentityDb db) : IRefreshTokenRepository
{
    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);

    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct)
    {
        var tokens = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.Revoke(now);
    }

    public async Task<int> RevokeAllForUserAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.Revoke(now);
        return tokens.Count;
    }

    public async Task<bool> RevokeUserFamilyAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct)
    {
        var tokens = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.FamilyId == familyId && t.RevokedAt == null && t.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var t in tokens) t.Revoke(now);
        return tokens.Count > 0;
    }

    public async Task<IReadOnlyList<RefreshToken>> ListActiveSessionsAsync(Guid userId, DateTime now, CancellationToken ct) =>
        await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .ToListAsync(ct);
}

internal sealed class EmailTokenRepository(IdentityDb db) : IEmailTokenRepository
{
    public void Add(EmailToken token) => db.EmailTokens.Add(token);

    public Task<EmailToken?> FindUsableAsync(Guid userId, EmailTokenPurpose purpose, string tokenHash, DateTime now, CancellationToken ct) =>
        db.EmailTokens.FirstOrDefaultAsync(
            t => t.UserId == userId && t.Purpose == purpose && t.TokenHash == tokenHash && t.UsedAt == null && t.ExpiresAt > now, ct);

    public async Task InvalidateOutstandingAsync(Guid userId, EmailTokenPurpose purpose, DateTime now, CancellationToken ct)
    {
        var open = await db.EmailTokens.Where(t => t.UserId == userId && t.Purpose == purpose && t.UsedAt == null).ToListAsync(ct);
        foreach (var t in open) t.MarkUsed(now);
    }
}

/// <summary>Adds the outbox row to the current unit of work, so it commits (or rolls back) with the state change.</summary>
internal sealed class EfOutbox(IdentityDb db, TimeProvider time) : IOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Add(IntegrationEvent e)
    {
        var activity = Activity.Current;
        db.Outbox.Add(new OutboxMessage
        {
            Id = Ids.NewGuidV7(),
            AggregateType = e.AggregateType,
            AggregateId = e.AggregateId,
            Topic = e.Topic,
            EventType = e.Type,
            Payload = JsonSerializer.Serialize(e.Data, Json),
            Headers = JsonSerializer.Serialize(new { source = "identity", correlationId = activity?.TraceId.ToString(), traceparent = activity?.Id }, Json),
            OccurredAt = time.GetUtcNow().UtcDateTime,
        });
    }
}
