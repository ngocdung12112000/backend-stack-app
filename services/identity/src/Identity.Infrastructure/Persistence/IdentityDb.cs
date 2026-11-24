using System.Text;
using Identity.Application.Abstractions;
using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MySqlConnector;

namespace Identity.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string Headers { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public string Consumer { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
}

/// <summary>EF Core context for the <c>identity</c> MySQL schema (docs/03-data-architecture.md section 2.1 and 2.7).</summary>
public sealed class IdentityDb(DbContextOptions<IdentityDb> options) : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // MySQL returns DATETIME(6) with Kind=Unspecified; everything in this service is UTC.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>().HaveColumnType("datetime(6)");
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>().HaveColumnType("datetime(6)");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Email).HasMaxLength(256).IsRequired();
            e.Property(u => u.NormalizedEmail).HasMaxLength(256).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(u => u.FullName).HasMaxLength(200);
            e.Property(u => u.Phone).HasMaxLength(30);
            e.Property(u => u.Status).HasConversion<string>().HasColumnType("enum('Active','Locked','Deleted')");
            e.Property(u => u.RowVersion).IsConcurrencyToken();
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.NormalizedEmail).IsUnique();
            e.HasMany(u => u.Roles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(u => u.Roles).HasField("_roles").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(50).IsRequired();
            e.HasIndex(r => r.Name).IsUnique();
            e.HasMany(r => r.Permissions).WithOne().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(r => r.Permissions).HasField("_permissions").UsePropertyAccessMode(PropertyAccessMode.Field);
            e.HasData(IdentitySeed.Roles.Select(r => new { r.Id, r.Name }));
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(r => new { r.UserId, r.RoleId });
            e.HasOne(r => r.Role).WithMany().HasForeignKey(r => r.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(p => new { p.RoleId, p.Permission });
            e.Property(p => p.Permission).HasMaxLength(100);
            e.HasData(IdentitySeed.Permissions.Select(p => new { p.RoleId, p.Permission }));
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.TokenHash).HasColumnType("char(64)").IsRequired();
            e.Property(t => t.CreatedIp).HasMaxLength(45);
            e.Property(t => t.UserAgent).HasMaxLength(300);
            // Two concurrent rotations of the same token: the second UPDATE finds revoked_at already set and fails.
            e.Property(t => t.RevokedAt).IsConcurrencyToken();
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.FamilyId);
            e.HasIndex(t => new { t.UserId, t.RevokedAt });
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EmailToken>(e =>
        {
            e.ToTable("email_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.Purpose).HasConversion<string>().HasColumnType("enum('Confirm','Reset')");
            e.Property(t => t.TokenHash).HasColumnType("char(64)").IsRequired();
            e.HasIndex(t => new { t.UserId, t.Purpose });
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(m => m.Id);
            e.Property(m => m.AggregateType).HasMaxLength(100);
            e.Property(m => m.AggregateId).HasMaxLength(64);
            e.Property(m => m.Topic).HasMaxLength(150);
            e.Property(m => m.EventType).HasMaxLength(150);
            e.Property(m => m.Payload).HasColumnType("json");
            e.Property(m => m.Headers).HasColumnType("json");
            e.Property(m => m.LastError).HasColumnType("text");
            e.HasIndex(m => m.ProcessedAt);
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(m => new { m.MessageId, m.Consumer });
            e.Property(m => m.Consumer).HasMaxLength(100);
        });

        ApplySnakeCaseColumnNames(b);
    }

    private static void ApplySnakeCaseColumnNames(ModelBuilder b)
    {
        foreach (var property in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
            property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<User>().Where(e => e.State == EntityState.Modified))
            entry.Entity.RowVersion++;
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    async Task IUnitOfWork.SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrentUpdateException(ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
        {
            throw new UniqueConstraintViolationException(ex);
        }
    }
}

internal sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

internal sealed class NullableUtcConverter()
    : ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
