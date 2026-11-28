using BuildingBlocks.Common;
using FluentValidation;
using Identity.Application.Abstractions;
using Identity.Domain;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Application.Users;

public static class UserMapping
{
    public static UserProfile ToProfile(this User u) =>
        new(u.Id, u.Email, u.FullName, u.Phone, u.EmailConfirmed, u.Status.ToString(), u.RoleNames, u.Permissions, u.CreatedAt, u.AvatarAssetId);
}

// ---------------------------------------------------------------- Profile

public sealed record GetMeQuery(Guid UserId) : IRequest<Result<UserProfile>>;

public sealed class GetMeHandler(IUserRepository users) : IRequestHandler<GetMeQuery, Result<UserProfile>>
{
    public async Task<Result<UserProfile>> Handle(GetMeQuery q, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(q.UserId, ct);
        return user is null || user.IsDeleted
            ? Result.Failure<UserProfile>(IdentityErrors.UserNotFound)
            : Result.Success(user.ToProfile());
    }
}

// ---------------------------------------------------------------- Admin (US-01.7, US-01.8)

public sealed record ListUsersQuery(string? Search, string? Role, string? Status, int Page, int PageSize)
    : IRequest<Result<PagedResult<UserProfile>>>;

public sealed class ListUsersValidator : AbstractValidator<ListUsersQuery>
{
    public ListUsersValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Status).Must(s => s is null || Enum.TryParse<UserStatus>(s, true, out _))
            .WithMessage("Status must be Active, Locked or Deleted.");
    }
}

public sealed class ListUsersHandler(IUserRepository users) : IRequestHandler<ListUsersQuery, Result<PagedResult<UserProfile>>>
{
    public async Task<Result<PagedResult<UserProfile>>> Handle(ListUsersQuery q, CancellationToken ct)
    {
        UserStatus? status = q.Status is null ? null : Enum.Parse<UserStatus>(q.Status, true);
        var page = await users.ListAsync(q.Search, q.Role, status, q.Page, q.PageSize, ct);
        return Result.Success(new PagedResult<UserProfile>(
            page.Items.Select(u => u.ToProfile()).ToList(), page.Page, page.PageSize, page.TotalCount));
    }
}

public sealed record SetRolesCommand(Guid UserId, IReadOnlyList<string> Roles) : IRequest<Result>;

public sealed class SetRolesValidator : AbstractValidator<SetRolesCommand>
{
    public SetRolesValidator() => RuleFor(x => x.Roles).NotNull().NotEmpty().WithMessage("At least one role is required.");
}

public sealed class SetRolesHandler(IUserRepository users, IRoleRepository roles, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<SetRolesCommand, Result>
{
    public async Task<Result> Handle(SetRolesCommand cmd, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(cmd.UserId, ct);
        if (user is null) return Result.Failure(IdentityErrors.UserNotFound);

        var wanted = await roles.GetByNamesAsync(cmd.Roles, ct);
        var unknown = cmd.Roles.FirstOrDefault(r => wanted.All(w => !string.Equals(w.Name, r, StringComparison.OrdinalIgnoreCase)));
        if (unknown is not null) return Result.Failure(IdentityErrors.UnknownRole(unknown));

        user.SetRoles(wanted, time.GetUtcNow().UtcDateTime);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record LockUserCommand(Guid UserId, Guid AdminId) : IRequest<Result>;

public sealed class LockUserHandler(
    IUserRepository users, IRefreshTokenRepository refreshTokens, IOutbox outbox, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<LockUserCommand, Result>
{
    public async Task<Result> Handle(LockUserCommand cmd, CancellationToken ct)
    {
        if (cmd.UserId == cmd.AdminId) return Result.Failure(IdentityErrors.CannotLockSelf);

        var user = await users.GetByIdAsync(cmd.UserId, ct);
        if (user is null || user.IsDeleted) return Result.Failure(IdentityErrors.UserNotFound);
        if (user.Status == UserStatus.Locked) return Result.Success();

        var now = time.GetUtcNow().UtcDateTime;
        user.Lock(now);
        await refreshTokens.RevokeAllForUserAsync(user.Id, now, ct); // BR-09: no more refreshes
        outbox.Add(new UserLockedV1(user.Id, cmd.AdminId, now));
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record UnlockUserCommand(Guid UserId) : IRequest<Result>;

public sealed class UnlockUserHandler(IUserRepository users, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<UnlockUserCommand, Result>
{
    public async Task<Result> Handle(UnlockUserCommand cmd, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(cmd.UserId, ct);
        if (user is null || user.IsDeleted) return Result.Failure(IdentityErrors.UserNotFound);

        user.Unlock(time.GetUtcNow().UtcDateTime);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ---------------------------------------------------------------- First admin (BR-08), used by the "create-admin" CLI mode

public sealed record CreateAdminCommand(string Email, string Password, string FullName) : IRequest<Result<Guid>>;

public sealed class CreateAdminHandler(
    IUserRepository users, IRoleRepository roles, IPasswordHasher hasher, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<CreateAdminCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateAdminCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (await users.EmailExistsAsync(User.NormalizeEmail(cmd.Email), ct))
            return Result.Failure<Guid>(IdentityErrors.EmailTaken);

        var customer = await roles.GetRequiredAsync(RoleNames.Customer, ct);
        var admin = await roles.GetRequiredAsync(RoleNames.Admin, ct);
        var user = User.Register(cmd.Email, cmd.FullName, hasher.Hash(cmd.Password), customer, now);
        user.SetRoles([customer, admin], now);
        user.ConfirmEmail(now);
        users.Add(user);
        await uow.SaveChangesAsync(ct);
        return Result.Success(user.Id);
    }
}

public sealed class CreateAdminValidator : AbstractValidator<CreateAdminCommand>
{
    public CreateAdminValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256).EmailAddress();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty().WithErrorCode(IdentityErrors.WeakPasswordCode).MeetsPasswordPolicy();
    }
}

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>MediatR handlers, validators, the validation pipeline behaviour and the email builder.</summary>
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        services.AddMediatR(c =>
        {
            c.RegisterServicesFromAssemblyContaining<GetMeQuery>();
            c.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        services.AddValidatorsFromAssemblyContaining<GetMeQuery>(includeInternalTypes: true);
        services.AddScoped<Auth.AuthEmails>();
        return services;
    }
}
