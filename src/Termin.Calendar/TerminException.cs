namespace Termin.Calendar;

/// <summary>The closed set of error codes a tool can return.</summary>
public static class ErrorCodes
{
    public const string Conflict = "conflict";
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string InvalidTime = "invalid_time";
    public const string InvalidInput = "invalid_input";
    public const string BackendUnavailable = "backend_unavailable";
}

/// <summary>
/// An error that is safe to show to the model. Messages must never contain
/// backend URLs, usernames, secrets or raw response bodies.
/// </summary>
public class TerminException(string code, string message, string? hint = null, object? current = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public string? Hint { get; } = hint;

    /// <summary>For <c>conflict</c>: the current version of the item.</summary>
    public object? Current { get; } = current;

    public static TerminException InvalidTime(string message, string? hint = null) =>
        new(ErrorCodes.InvalidTime, message, hint);

    public static TerminException Invalid(string message, string? hint = null) =>
        new(ErrorCodes.InvalidInput, message, hint);

    public static TerminException NotFound(string message, string? hint = null) =>
        new(ErrorCodes.NotFound, message, hint);
}
