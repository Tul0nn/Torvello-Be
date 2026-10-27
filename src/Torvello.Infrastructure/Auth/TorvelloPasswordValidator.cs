using Microsoft.AspNetCore.Identity;
using Torvello.Domain;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Auth;

/// <summary>Applies the shared Torvello password policy to every password Identity sets.</summary>
public sealed class TorvelloPasswordValidator : IPasswordValidator<AppUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        var problem = PasswordRules.Problem(password ?? "", user.Email);
        return Task.FromResult(problem is null
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError { Code = "TorvelloPasswordPolicy", Description = problem }));
    }
}
