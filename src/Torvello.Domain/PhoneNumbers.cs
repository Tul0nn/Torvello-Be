using System.Text.RegularExpressions;

namespace Torvello.Domain;

/// <summary>Bangladeshi mobile numbers, stored as +8801XXXXXXXXX. Same rules as the till.</summary>
public static partial class PhoneNumbers
{
    /// <summary>Accepts 01712345678, +8801712345678, 8801712345678, with spaces or dashes. Null when not a BD mobile.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var compact = input.Replace(" ", "").Replace("-", "");
        var match = Mobile().Match(compact);
        return match.Success ? "+880" + match.Groups[1].Value : null;
    }

    /// <summary>+8801712345678 → +8801*****678, for people who need to recognise a customer but not contact them.</summary>
    public static string Mask(string normalized) =>
        normalized.Length > 8 ? normalized[..5] + new string('*', normalized.Length - 8) + normalized[^3..] : "***";

    // Local form 01XXXXXXXXX, or international +880 1XXXXXXXXX (the trunk 0 is dropped after the country code).
    [GeneratedRegex(@"^(?:\+?880|0)(1[3-9]\d{8})$")]
    private static partial Regex Mobile();
}

public static class CalendarDays
{
    /// <summary>Month/day combinations a birthday can have; 29 February is allowed.</summary>
    public static bool IsValid(int? month, int? day)
    {
        if (month is null && day is null) return true;
        if (month is not (>= 1 and <= 12) || day is null || day < 1) return false;
        return day <= DateTime.DaysInMonth(2024, month.Value); // 2024 is a leap year
    }
}
