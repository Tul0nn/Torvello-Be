using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Auth;

public static class TorvelloClaims
{
    public const string SecurityStamp = "sstamp";
    public const string Role = "role";

    /// <summary>Present while a temporary password must be replaced; the API then allows only a few endpoints.</summary>
    public const string PasswordChangeRequired = "pwd_change";
}

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(AppUser user, string role)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(TorvelloClaims.Role, role),
            // Lets the API reject tokens issued before a password change, role change or deactivation.
            new(TorvelloClaims.SecurityStamp, user.SecurityStamp ?? ""),
        };
        if (user.MustChangePassword) claims.Add(new Claim(TorvelloClaims.PasswordChangeRequired, "1"));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    public static SymmetricSecurityKey SigningKey(JwtOptions options)
    {
        var bytes = Encoding.UTF8.GetBytes(options.SigningKey);
        if (bytes.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes. Set it with user-secrets or an environment variable.");
        return new SymmetricSecurityKey(bytes);
    }

    public static string NewRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
