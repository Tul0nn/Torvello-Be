namespace Torvello.Application.Common;

/// <summary>An error whose message is safe to return to the client.</summary>
public sealed class AppException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static AppException BadRequest(string message) => new(400, message);
    public static AppException Unauthorized(string message) => new(401, message);
    public static AppException Forbidden(string message) => new(403, message);
    public static AppException NotFound(string message) => new(404, message);
    public static AppException Conflict(string message) => new(409, message);
}
