using BuildingBlocks.Common;
using BuildingBlocks.Security;
using Identity.Application;
using Identity.Application.Auth;
using Identity.Application.Users;
using Identity.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Identity.Api.Endpoints;

public sealed record RegisterRequest(string Email, string Password, string FullName);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string? RefreshToken, bool AllDevices = false);
public sealed record ConfirmEmailRequest(Guid UserId, string Token);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record SetRolesRequest(IReadOnlyList<string> Roles);

public static class AuthEndpoints
{
    public static void MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth");

        auth.MapPost("/register", async (RegisterRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var result = await s.Send(new RegisterCommand(r.Email, r.Password, r.FullName), ct);
                return result.IsSuccess ? Results.Created("/api/v1/users/me", result.Value) : Problems.From(result.Error, ctx);
            })
            .WithName("Register").WithSummary("Register a customer account and send the confirmation email")
            .Produces<RegisterResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapPost("/login", async (LoginRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.OkOr(await s.Send(new LoginCommand(r.Email, r.Password, ctx.Client()), ct), ctx))
            .WithName("Login").WithSummary("Exchange email and password for an access and refresh token")
            .Produces<LoginResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status423Locked)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/refresh", async (RefreshRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.OkOr(await s.Send(new RefreshCommand(r.RefreshToken, ctx.Client()), ct), ctx))
            .WithName("Refresh").WithSummary("Rotate the refresh token and issue a new token pair")
            .Produces<TokenPairResponse>().ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", async ([FromBody] LogoutRequest? r, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var u = ctx.User;
                var result = await s.Send(new LogoutCommand(u.UserId(), r?.RefreshToken, r?.AllDevices ?? false, u.Jti(), u.ExpiresAt()), ct);
                return Problems.NoContentOr(result, ctx);
            })
            .RequireAuthorization()
            .WithName("Logout").WithSummary("Revoke this session (or all sessions) and blacklist the access token")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/confirm-email", async (ConfirmEmailRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.NoContentOr(await s.Send(new ConfirmEmailCommand(r.UserId, r.Token), ct), ctx))
            .WithName("ConfirmEmail").WithSummary("Confirm an email address with the emailed token")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest);

        auth.MapPost("/resend-confirmation", async (ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.NoContentOr(await s.Send(new ResendConfirmationCommand(ctx.User.UserId()), ct), ctx))
            .RequireAuthorization()
            .WithName("ResendConfirmation").WithSummary("Send a fresh confirmation email")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/forgot-password", async (ForgotPasswordRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var result = await s.Send(new ForgotPasswordCommand(r.Email), ct);
                return result.IsSuccess ? Results.Accepted() : Problems.From(result.Error, ctx);
            })
            .WithName("ForgotPassword").WithSummary("Email a reset link if the account exists (always 202)")
            .Produces(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest);

        auth.MapPost("/reset-password", async (ResetPasswordRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.NoContentOr(await s.Send(new ResetPasswordCommand(r.Email, r.Token, r.NewPassword), ct), ctx))
            .WithName("ResetPassword").WithSummary("Set a new password with the emailed token; revokes all sessions")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest);

        auth.MapPost("/change-password", async (ChangePasswordRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.NoContentOr(await s.Send(new ChangePasswordCommand(ctx.User.UserId(), r.CurrentPassword, r.NewPassword), ct), ctx))
            .RequireAuthorization()
            .WithName("ChangePassword").WithSummary("Change the password of the signed-in user")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized);

        app.MapGet("/.well-known/jwks.json", (SigningKeyStore keys, HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "public, max-age=300";
                return Results.Json(keys.Jwks);
            })
            .WithName("Jwks").WithTags("Auth").WithSummary("Public signing keys (JWKS)")
            .Produces(StatusCodes.Status200OK);

        var users = app.MapGroup("/api/v1/users").WithTags("Users").RequireAuthorization();

        users.MapGet("/me", async (ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.OkOr(await s.Send(new GetMeQuery(ctx.User.UserId()), ct), ctx))
            .WithName("GetMe").WithSummary("Current user profile")
            .Produces<UserProfile>().ProducesProblem(StatusCodes.Status401Unauthorized);

        users.MapGet("/me/sessions", async (ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.OkOr(await s.Send(new ListSessionsQuery(ctx.User.UserId()), ct), ctx))
            .WithName("ListSessions").WithSummary("Active sessions (refresh-token families)")
            .Produces<IReadOnlyList<SessionDto>>().ProducesProblem(StatusCodes.Status401Unauthorized);

        users.MapDelete("/me/sessions/{familyId:guid}", async (Guid familyId, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.NoContentOr(await s.Send(new RevokeSessionCommand(ctx.User.UserId(), familyId), ct), ctx))
            .WithName("RevokeSession").WithSummary("Sign out one session")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        var admin = app.MapGroup("/api/v1/admin/users").WithTags("Admin users");

        admin.MapGet("", async (ISender s, HttpContext ctx, string? search, string? role, string? status, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
                Problems.OkOr(await s.Send(new ListUsersQuery(search, role, status, page, pageSize), ct), ctx))
            .RequireAuthorization(Permissions.Policy("users:read"))
            .WithName("AdminListUsers").WithSummary("Paged user list")
            .Produces<PagedResult<UserProfile>>().ProducesProblem(StatusCodes.Status403Forbidden);

        admin.MapPut("/{id:guid}/roles", async (Guid id, SetRolesRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.NoContentOr(await s.Send(new SetRolesCommand(id, r.Roles), ct), ctx))
            .RequireAuthorization(Permissions.Policy("users:write"))
            .WithName("AdminSetRoles").WithSummary("Replace the roles of a user")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/{id:guid}/lock", async (Guid id, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.NoContentOr(await s.Send(new LockUserCommand(id, ctx.User.UserId()), ct), ctx))
            .RequireAuthorization(Permissions.Policy("users:write"))
            .WithName("AdminLockUser").WithSummary("Lock a user and revoke their sessions")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapPost("/{id:guid}/unlock", async (Guid id, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.NoContentOr(await s.Send(new UnlockUserCommand(id), ct), ctx))
            .RequireAuthorization(Permissions.Policy("users:write"))
            .WithName("AdminUnlockUser").WithSummary("Unlock a user")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
    }
}
