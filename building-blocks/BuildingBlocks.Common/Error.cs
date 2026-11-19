namespace BuildingBlocks.Common;

public enum ErrorType { Validation, NotFound, Conflict, Unauthorized, Forbidden, Failure }

/// <summary>Service-prefixed error code, e.g. INVENTORY_INSUFFICIENT_STOCK. Maps to RFC 7807 ProblemDetails.</summary>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Failure)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
}
