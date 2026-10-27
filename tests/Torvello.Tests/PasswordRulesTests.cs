using Torvello.Domain;

namespace Torvello.Tests;

public sealed class PasswordRulesTests
{
    [Fact]
    public void Accepts_a_reasonable_password() => Assert.Null(PasswordRules.Problem("coffee4Dhaka", "rahim@torvello.com"));

    [Theory]
    [InlineData("ab12", "at least 8")]
    [InlineData("onlyletters", "letters and numbers")]
    [InlineData("1234567890", "letters and numbers")]
    [InlineData("Password123", "too common")]
    [InlineData("Torvello@2026", "too common")]
    public void Rejects_weak_passwords(string password, string expected) =>
        Assert.Contains(expected, PasswordRules.Problem(password));

    [Fact]
    public void Rejects_passwords_containing_the_email_name() =>
        Assert.Contains("email name", PasswordRules.Problem("rahim2026x", "rahim@torvello.com"));
}
