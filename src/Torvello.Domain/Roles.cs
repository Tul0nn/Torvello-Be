namespace Torvello.Domain;

/// <summary>Staff roles. Same meaning as on the cafe till.</summary>
public static class Roles
{
    public const string Owner = "Owner";
    public const string Manager = "Manager";
    public const string Cashier = "Cashier";

    public static readonly IReadOnlyList<string> All = [Owner, Manager, Cashier];

    public static bool IsValid(string? role) => role is not null && All.Contains(role);
}
