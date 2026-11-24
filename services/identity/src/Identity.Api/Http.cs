using System.Security.Claims;
using BuildingBlocks.Common;
using FluentValidation;
using Identity.Application;
using Identity.Application.Abstractions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Identity.Api;

/// <summary>Maps application errors to RFC 7807 ProblemDetails with the service-prefixed <c>code</c> (docs/05 section 3).</summary>
public static class Problems
{
    public static IResult From(Error error, HttpContext ctx)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Locked => StatusCodes.Status423Locked,
            ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status500InternalServerError,
        };

        if (error.Type == ErrorType.TooManyRequests)
        {
            var window = ctx.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.IpWindowMinutes;
            ctx.Response.Headers.RetryAfter = (window * 60).ToString();
        }

        return Build(ctx, status, error.Code, error.Message, null);
    }

    public static IResult Build(HttpContext ctx, int status, string code, string detail, IDictionary<string, string[]>? errors)
    {
        var problem = new ProblemDetails
        {
            Type = $"https://docs.stackshop.local/errors/{code.ToLowerInvariant().Replace('_', '-')}",
            Title = Title(code),
            Status = status,
            Detail = detail,
            Instance = ctx.Request.Path,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? ctx.TraceIdentifier;
        if (errors is not null) problem.Extensions["errors"] = errors;
        return Results.Problem(problem);
    }

    private static string Title(string code) =>
        string.Join(' ', code.Split('_').Skip(1).Select(w => w.ToLowerInvariant())) is { Length: > 0 } t
            ? char.ToUpperInvariant(t[0]) + t[1..]
            : code;

    /// <summary>204 on success, ProblemDetails on failure.</summary>
    public static IResult NoContentOr(Result result, HttpContext ctx) =>
        result.IsSuccess ? Results.NoContent() : From(result.Error, ctx);

    public static IResult OkOr<T>(Result<T> result, HttpContext ctx) =>
        result.IsSuccess ? Results.Ok(result.Value) : From(result.Error, ctx);
}

/// <summary>Turns validation failures into 400 (with an <c>errors</c> dictionary) and unexpected faults into a safe 500.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception exception, CancellationToken ct)
    {
        IResult result;
        switch (exception)
        {
            case ValidationException v:
                var errors = v.Errors
                    .GroupBy(e => Camel(e.PropertyName))
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
                var code = v.Errors.Any(e => e.ErrorCode == IdentityErrors.WeakPasswordCode)
                    ? IdentityErrors.WeakPasswordCode
                    : "IDENTITY_VALIDATION_FAILED";
                result = Problems.Build(ctx, StatusCodes.Status400BadRequest, code, "One or more validation errors occurred.", errors);
                break;
            case ConcurrentUpdateException:
                result = Problems.Build(ctx, StatusCodes.Status409Conflict, "IDENTITY_CONCURRENT_UPDATE",
                    "The record was changed by another request. Retry.", null);
                break;
            case BadHttpRequestException bad:
                result = Problems.Build(ctx, StatusCodes.Status400BadRequest, "IDENTITY_BAD_REQUEST", "Malformed request.", null);
                logger.LogDebug(bad, "Bad request");
                break;
            default:
                logger.LogError(exception, "Unhandled exception");
                result = Problems.Build(ctx, StatusCodes.Status500InternalServerError, "IDENTITY_INTERNAL_ERROR", "An unexpected error occurred.", null);
                break;
        }

        await result.ExecuteAsync(ctx);
        return true;
    }

    private static string Camel(string name) => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst("sub")?.Value, out var id) ? id : throw new InvalidOperationException("Missing sub claim.");

    public static string? Jti(this ClaimsPrincipal user) => user.FindFirst("jti")?.Value;

    public static DateTime? ExpiresAt(this ClaimsPrincipal user) =>
        long.TryParse(user.FindFirst("exp")?.Value, out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : null;

    public static ClientInfo Client(this HttpContext ctx) =>
        new(ctx.Connection.RemoteIpAddress?.ToString(), ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null);
}
