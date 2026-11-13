using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Torvello.Domain;
using Torvello.Infrastructure.Auth;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Api.Infrastructure;

public static class Security
{
    public static void ConfigureSecurity(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var development = builder.Environment.IsDevelopment();
        var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
        var publicOrigins = config.GetSection("PublicSite:Origins").Get<string[]>() ?? [];
        if (origins.Concat(publicOrigins).Any(origin => origin.Contains('*') || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo) || (uri.Scheme != "https" && (!development || uri.Scheme != "http"))))
            throw new InvalidOperationException("Cors:Origins and PublicSite:Origins must contain explicit HTTPS origins (HTTP is allowed in Development)");
        if (!development)
        {
            if (Encoding.UTF8.GetByteCount(config["Jwt:SigningKey"] ?? "") < 32)
                throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes");
            var connection = config.GetConnectionString("Torvello");
            if (!string.IsNullOrWhiteSpace(connection) &&
                string.Equals(new NpgsqlConnectionStringBuilder(connection).Username, "postgres", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use a least-privilege database user instead of postgres");
        }
        builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
        var keysPath = config["DataProtection:KeysPath"];
        if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(keysPath))
            throw new InvalidOperationException("DataProtection:KeysPath is required in Production");
        var protection = builder.Services.AddDataProtection().SetApplicationName("Torvello");
        if (string.IsNullOrWhiteSpace(keysPath) && development) keysPath = "App_Data/keys";
        if (!string.IsNullOrWhiteSpace(keysPath))
            protection.PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keysPath, builder.Environment.ContentRootPath)));
        var thumbprint = config["DataProtection:CertificateThumbprint"];
        if (!string.IsNullOrWhiteSpace(thumbprint)) protection.ProtectKeysWithCertificate(thumbprint);
        else if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
            options.AddPolicy("PublicMenu", policy => policy.WithOrigins(publicOrigins).WithMethods("GET")
                .WithHeaders("If-None-Match").WithExposedHeaders("ETag", "Cache-Control"));
        });
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers.Remove("Server");
                return Task.CompletedTask;
            });
            await next();
        });

    public static IApplicationBuilder UseTwoFactorGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true &&
                context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Security:RequireTwoFactorForManagers", true) &&
                (context.User.IsInRole(Roles.Owner) || context.User.IsInRole(Roles.Manager)))
            {
                var path = context.Request.Path.Value ?? "";
                var allowed = (HttpMethods.IsGet(context.Request.Method) && path.Equals("/health", StringComparison.OrdinalIgnoreCase)) ||
                    (HttpMethods.IsGet(context.Request.Method) && path.Equals("/api/public/menu", StringComparison.OrdinalIgnoreCase)) ||
                    path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/api/auth/refresh", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/api/auth/change-password", StringComparison.OrdinalIgnoreCase) ||
                    ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsPost(context.Request.Method)) &&
                        (path.Equals("/api/auth/2fa", StringComparison.OrdinalIgnoreCase) ||
                         path.StartsWith("/api/auth/2fa/", StringComparison.OrdinalIgnoreCase)));
                var users = context.RequestServices.GetRequiredService<UserManager<AppUser>>();
                var user = await users.FindByIdAsync(context.User.UserId().ToString());
                if (user is { TwoFactorEnabled: false } && !allowed)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = 403, Title = "Set up two-step sign-in first" },
                        options: null, contentType: "application/problem+json");
                    return;
                }
            }
            await next();
        });
}
