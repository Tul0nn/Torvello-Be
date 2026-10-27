namespace Torvello.Infrastructure;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "torvello-api";
    public string Audience { get; set; } = "torvello-clients";

    /// <summary>At least 32 bytes. Keep it in user-secrets or an environment variable, never in git.</summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>"Pickup" writes emails to a folder (development); "Smtp" sends them.</summary>
    public string Provider { get; set; } = "Pickup";
    public string FromAddress { get; set; } = "no-reply@torvello.local";
    public string FromName { get; set; } = "Torvello";
    public string PickupDirectory { get; set; } = "mail-pickup";

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
}

public sealed class AppLinkOptions
{
    public const string Section = "AppLinks";

    /// <summary>Base URL of the web app that hosts the accept-invite and reset-password pages.</summary>
    public string WebBaseUrl { get; set; } = "http://localhost:5173";
}

public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";

    /// <summary>Creates the first owner when the database has no users. The password must be changed at first sign-in.</summary>
    public string? OwnerEmail { get; set; }
    public string? OwnerPassword { get; set; }
    public string OwnerName { get; set; } = "Owner";
}
