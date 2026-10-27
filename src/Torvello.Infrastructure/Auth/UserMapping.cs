using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;
using Torvello.Application.Auth;
using Torvello.Application.Common;
using Torvello.Domain;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Auth;

internal static class UserMapping
{
    public static async Task<string> RoleOfAsync(this UserManager<AppUser> users, AppUser user)
    {
        var roles = await users.GetRolesAsync(user);
        return roles.FirstOrDefault(Roles.IsValid) ?? Roles.Cashier;
    }

    public static async Task<UserDto> ToDtoAsync(this UserManager<AppUser> users, AppUser user) =>
        new(user.Id, user.Email ?? "", user.DisplayName, await users.RoleOfAsync(user), user.IsActive, user.EmailConfirmed, user.MustChangePassword);

    /// <summary>Identity tokens contain characters that break in URLs, so links carry them base64url-encoded.</summary>
    public static string EncodeToken(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    public static string DecodeToken(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            throw AppException.BadRequest("This link is invalid or has expired");
        }
    }

    public static AppException ToAppException(this IdentityResult result) =>
        AppException.BadRequest(string.Join(" ", result.Errors.Select(e => e.Description)));
}
