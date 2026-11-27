using Catalog.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Catalog.Api;

/// <summary>Maps business failures to RFC 7807 ProblemDetails with the service-prefixed <c>code</c> (docs/05 section 3).</summary>
public static class Problems
{
    public static IResult Build(HttpContext ctx, int status, string code, string detail, IReadOnlyDictionary<string, string[]>? errors = null)
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

    public static int StatusOf(FailureKind kind) => kind switch
    {
        FailureKind.Validation => StatusCodes.Status400BadRequest,
        FailureKind.NotFound => StatusCodes.Status404NotFound,
        FailureKind.Conflict => StatusCodes.Status409Conflict,
        FailureKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        FailureKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        FailureKind.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string Title(string code) =>
        string.Join(' ', code.Split('_').Skip(1).Select(w => w.ToLowerInvariant())) is { Length: > 0 } t
            ? char.ToUpperInvariant(t[0]) + t[1..]
            : code;
}

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception exception, CancellationToken ct)
    {
        IResult result;
        switch (exception)
        {
            case CatalogException c:
                result = Problems.Build(ctx, Problems.StatusOf(c.Kind), c.Code, c.Message, c.Errors);
                break;
            case ValidationException v:
                var errors = v.Errors.GroupBy(e => Camel(e.PropertyName))
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
                result = Problems.Build(ctx, StatusCodes.Status400BadRequest, CatalogErrors.ValidationFailed, "One or more validation errors occurred.", errors);
                break;
            case BadHttpRequestException bad:
                logger.LogDebug(bad, "Bad request");
                result = Problems.Build(ctx, StatusCodes.Status400BadRequest, "CATALOG_BAD_REQUEST", "Malformed request.");
                break;
            default:
                logger.LogError(exception, "Unhandled exception");
                result = Problems.Build(ctx, StatusCodes.Status500InternalServerError, "CATALOG_INTERNAL_ERROR", "An unexpected error occurred.");
                break;
        }

        await result.ExecuteAsync(ctx);
        return true;
    }

    /// <summary>camelCase field path; the command wrapper segments ("Details.", "Body.") are not part of the public request shape.</summary>
    private static string Camel(string name)
    {
        foreach (var wrapper in new[] { "Details.", "Body." })
            if (name.StartsWith(wrapper, StringComparison.Ordinal)) name = name[wrapper.Length..];
        return string.Join('.', name.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
    }
}

public static class ETags
{
    public static string For(long version) => $"\"{version}\"";

    /// <summary>BR-08: parses <c>If-Match: "7"</c>. Missing is 428 (CATALOG_IF_MATCH_REQUIRED), unparseable is 400.</summary>
    public static long RequireIfMatch(HttpRequest request)
    {
        var raw = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            throw new CatalogException(CatalogErrors.IfMatchRequired, "Send the current version in the If-Match header.", FailureKind.PreconditionRequired);

        var token = raw.Trim();
        if (token.StartsWith("W/", StringComparison.Ordinal)) token = token[2..];
        token = token.Trim('"');
        return long.TryParse(token, out var version) && version > 0
            ? version
            : throw CatalogException.Invalid("If-Match", "If-Match must be the version from the ETag, e.g. \"7\".");
    }
}
