using FluentValidation;
using Torvello.Application.Auth;
using Torvello.Application.Staff;
using Torvello.Domain;

namespace Torvello.Application.Validation;

internal static class Rules
{
    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(254).EmailAddress().WithMessage("Enter a valid email address");

    /// <summary>Applies the shared password policy; the email (if any) is used to reject passwords containing it.</summary>
    public static IRuleBuilderOptionsConditions<T, string> StrongPassword<T>(this IRuleBuilder<T, string> rule, Func<T, string?>? email = null) =>
        rule.Custom((password, context) =>
        {
            var problem = PasswordRules.Problem(password ?? "", email?.Invoke(context.InstanceToValidate));
            if (problem is not null) context.AddFailure(problem);
        });
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(PasswordRules.MaxLength);
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(200);
}

public sealed class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(200);
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator() => RuleFor(x => x.Email).ValidEmail();
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Token).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.NewPassword).StrongPassword(x => x.Email);
    }
}

public sealed class AcceptInviteRequestValidator : AbstractValidator<AcceptInviteRequest>
{
    public AcceptInviteRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Token).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.Password).StrongPassword(x => x.Email);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(PasswordRules.MaxLength);
        RuleFor(x => x.NewPassword).StrongPassword();
        RuleFor(x => x.NewPassword).NotEqual(x => x.CurrentPassword).WithMessage("Choose a password different from the current one");
    }
}

public sealed class InviteStaffRequestValidator : AbstractValidator<InviteStaffRequest>
{
    public InviteStaffRequestValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Role).Must(Roles.IsValid).WithMessage($"Role must be one of: {string.Join(", ", Roles.All)}");
    }
}

public sealed class UpdateStaffRequestValidator : AbstractValidator<UpdateStaffRequest>
{
    public UpdateStaffRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Role).Must(Roles.IsValid).WithMessage($"Role must be one of: {string.Join(", ", Roles.All)}");
    }
}
