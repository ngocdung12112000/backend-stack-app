using Identity.Application;
using Identity.Application.Addresses;
using Identity.Application.Users;
using MediatR;

namespace Identity.Api.Endpoints;

/// <summary>PATCH body: omitted or null fields stay unchanged; <c>phone: ""</c> and the all-zero <c>avatarAssetId</c> clear the value.</summary>
public sealed record UpdateProfileRequest(string? FullName, string? Phone, Guid? AvatarAssetId);
public sealed record DeleteAccountRequest(string Password);

/// <summary>F02: profile update, account deletion and the address book.</summary>
public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/v1/users").WithTags("Users").RequireAuthorization();

        users.MapPatch("/me", async (UpdateProfileRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.OkOr(await s.Send(new UpdateProfileCommand(ctx.User.UserId(), r.FullName, r.Phone, r.AvatarAssetId), ct), ctx))
            .WithName("UpdateMe").WithSummary("Update name, phone and avatar of the current user (US-02.1)")
            .Produces<UserProfile>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized);

        users.MapDelete("/me", async ([Microsoft.AspNetCore.Mvc.FromBody] DeleteAccountRequest r, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var u = ctx.User;
                var result = await s.Send(new DeleteAccountCommand(u.UserId(), r.Password, u.Jti(), u.ExpiresAt()), ct);
                return result.IsSuccess ? Results.Accepted() : Problems.From(result.Error, ctx);
            })
            .WithName("DeleteMe").WithSummary("Soft-delete and anonymise the account after password confirmation (US-02.4)")
            .Produces(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized);

        // Canonical path from the spec; /users/me/addresses is a hidden alias for clients that nest it under the profile.
        MapAddresses(app.MapGroup("/api/v1/addresses").WithTags("Addresses"), "/api/v1/addresses", hidden: false);
        MapAddresses(app.MapGroup("/api/v1/users/me/addresses").WithTags("Addresses"), "/api/v1/users/me/addresses", hidden: true);
    }

    private static void MapAddresses(RouteGroupBuilder g, string basePath, bool hidden)
    {
        g.RequireAuthorization();
        var prefix = hidden ? "Alias" : "";

        T Tag<T>(T b) where T : IEndpointConventionBuilder
        {
            if (hidden) b.WithMetadata(new Microsoft.AspNetCore.Routing.ExcludeFromDescriptionAttribute());
            return b;
        }

        Tag(g.MapGet("", async (ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.OkOr(await s.Send(new ListAddressesQuery(ctx.User.UserId()), ct), ctx))
            .WithName(prefix + "ListAddresses").WithSummary("List the caller's addresses, default first")
            .Produces<IReadOnlyList<AddressDto>>().ProducesProblem(StatusCodes.Status401Unauthorized));

        Tag(g.MapPost("", async (AddressInput r, ISender s, HttpContext ctx, CancellationToken ct) =>
            {
                var result = await s.Send(new CreateAddressCommand(ctx.User.UserId(), r), ct);
                return result.IsSuccess ? Results.Created($"{basePath}/{result.Value.Id}", result.Value) : Problems.From(result.Error, ctx);
            })
            .WithName(prefix + "CreateAddress").WithSummary("Add an address; the first one becomes the default (max 10)")
            .Produces<AddressDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict));

        Tag(g.MapPut("/{id:guid}", async (Guid id, AddressInput r, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.OkOr(await s.Send(new ReplaceAddressCommand(ctx.User.UserId(), id, r), ct), ctx))
            .WithName(prefix + "ReplaceAddress").WithSummary("Replace the fields of one of the caller's addresses")
            .Produces<AddressDto>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound));

        Tag(g.MapDelete("/{id:guid}", async (Guid id, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.NoContentOr(await s.Send(new DeleteAddressCommand(ctx.User.UserId(), id), ct), ctx))
            .WithName(prefix + "DeleteAddress").WithSummary("Delete an address; deleting the default promotes the most recently updated one")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound));

        Tag(g.MapPost("/{id:guid}/default", async (Guid id, ISender s, HttpContext ctx, CancellationToken ct) =>
                Problems.OkOr(await s.Send(new SetDefaultAddressCommand(ctx.User.UserId(), id), ct), ctx))
            .WithName(prefix + "SetDefaultAddress").WithSummary("Make this address the default; the previous default is unset")
            .Produces<AddressDto>().ProducesProblem(StatusCodes.Status404NotFound));
    }
}
