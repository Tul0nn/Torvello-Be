using FluentValidation;
using Torvello.Application.Auth;
using Torvello.Application.Staff;
using Torvello.Application.Sync;
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

public sealed class CreateDeviceRequestValidator : AbstractValidator<CreateDeviceRequest>
{
    public CreateDeviceRequestValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
}

public sealed class SyncPushRequestValidator : AbstractValidator<SyncPushRequest>
{
    public const int MaxEvents = 200;

    public SyncPushRequestValidator()
    {
        RuleFor(x => x.Events).NotNull().Must(e => e.Count is > 0 and <= MaxEvents)
            .WithMessage($"Send between 1 and {MaxEvents} events");
        RuleForEach(x => x.Events).ChildRules(e =>
        {
            e.RuleFor(x => x.EventId).NotEmpty();
            e.RuleFor(x => x.EventType).NotEmpty().MaximumLength(60);
        });
    }
}

public sealed class PolicyInputValidator : AbstractValidator<Hr.PolicyInput>
{
    public PolicyInputValidator()
    {
        RuleFor(x => x.Title).Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("Title is required").MaximumLength(120);
        RuleFor(x => x.Category).Must(c => !string.IsNullOrWhiteSpace(c)).WithMessage("Category is required").MaximumLength(40);
        RuleFor(x => x.Body).Must(b => !string.IsNullOrWhiteSpace(b)).WithMessage("Policy text is required").MaximumLength(50_000);
    }
}

public sealed class CustomerUpdateValidator : AbstractValidator<People.CustomerUpdate>
{
    public CustomerUpdateValidator()
    {
        RuleFor(x => x.Name).MaximumLength(60);
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x).Must(x => CalendarDays.IsValid(x.BirthdayMonth, x.BirthdayDay)).WithMessage("Birthday is not a real date");
        RuleFor(x => x).Must(x => CalendarDays.IsValid(x.AnniversaryMonth, x.AnniversaryDay)).WithMessage("Anniversary is not a real date");
    }
}

public sealed class EmployeeProfileUpdateValidator : AbstractValidator<People.EmployeeProfileUpdate>
{
    public EmployeeProfileUpdateValidator()
    {
        RuleFor(x => x.Position).MaximumLength(60);
        RuleFor(x => x.Address).MaximumLength(300);
        RuleFor(x => x.EmergencyContactName).MaximumLength(60);
        RuleFor(x => x.Notes).MaximumLength(1000);
        // Bangladeshi NID numbers are 10, 13 or 17 digits.
        RuleFor(x => x.NationalId)
            .Must(id => string.IsNullOrWhiteSpace(id) || System.Text.RegularExpressions.Regex.IsMatch(id.Replace(" ", ""), @"^(\d{10}|\d{13}|\d{17})$"))
            .WithMessage("National ID must be 10, 13 or 17 digits");
        RuleFor(x => x.DateOfBirth).Must(d => d is null || d.Value.Year >= 1930).WithMessage("Date of birth looks wrong");
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
