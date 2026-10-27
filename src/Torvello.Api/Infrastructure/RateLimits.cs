namespace Torvello.Api.Infrastructure;

public static class RateLimits
{
    /// <summary>Per-IP limit on sign-in and password endpoints, against password guessing.</summary>
    public const string Auth = "auth";
}
