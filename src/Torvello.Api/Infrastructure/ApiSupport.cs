using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Torvello.Application.Auth;
using Torvello.Application.Common;
using Torvello.Infrastructure.Auth;

namespace Torvello.Api.Infrastructure;

/// <summary>Turns AppException into a ProblemDetails response; anything else becomes a plain 500.</summary>
public sealed class AppExceptionHandler(IProblemDetailsService problems, ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception is AppException app
            ? (app.StatusCode, app.Message)
            : (StatusCodes.Status500InternalServerError, "Something went wrong. Nothing was saved.");
        if (exception is not AppException) logger.LogError(exception, "Unhandled error");

        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = status, Title = title },
            Exception = exception,
        });
    }
}

/// <summary>Marks endpoints a user may call while their temporary password still has to be changed.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowWhilePasswordChangeRequiredAttribute : Attribute;

public static class HttpContextExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw AppException.Unauthorized("Please sign in again");

    public static RequestContext ToRequestContext(this HttpContext context) =>
        new(context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString());

    /// <summary>Blocks everything except a few endpoints until the temporary password is replaced.</summary>
    public static IApplicationBuilder UsePasswordChangeGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var mustChange = context.User.HasClaim(TorvelloClaims.PasswordChangeRequired, "1");
            var allowed = context.GetEndpoint()?.Metadata.GetMetadata<AllowWhilePasswordChangeRequiredAttribute>() is not null;
            if (mustChange && !allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new ProblemDetails { Status = 403, Title = "Set a new password first" },
                    options: null,
                    contentType: "application/problem+json");
                return;
            }
            await next();
        });
}
