using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Torvello.Application.Auth;
using Torvello.Application.Staff;
using Torvello.Domain;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Tests;

public sealed class SecurityTests
{
    [Fact]
    public async Task Recovery_replacement_and_disable_revoke_sessions_and_audit_changes()
    {
        using var app = new TestApp();
        var (owner, _) = await app.OwnerAsync();
        var setupResponse = await owner.PostAsync("/api/auth/2fa/setup", null);
        setupResponse.EnsureSuccessStatusCode();
        var setup = (await setupResponse.Content.ReadFromJsonAsync<TwoFactorSetup>())!;
        var enable = await owner.PostAsJsonAsync("/api/auth/2fa/enable", new TwoFactorCodeRequest(Totp(setup.SharedKey)));
        enable.EnsureSuccessStatusCode();
        var oldCodes = (await enable.Content.ReadFromJsonAsync<RecoveryCodesResponse>())!.RecoveryCodes;
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        async Task<string> Challenge()
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(TestApp.OwnerEmail, "Espresso4Boss"));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<TwoFactorChallenge>())!.TwoFactorToken;
        }
        var challenge = await Challenge();
        var login = await client.PostAsJsonAsync("/api/auth/login/2fa", new LoginTwoFactorRequest(challenge, Totp(setup.SharedKey)));
        login.EnsureSuccessStatusCode();
        var session = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;
        using var authed = app.ClientWith(session.AccessToken);
        var status = (await authed.GetFromJsonAsync<TwoFactorStatus>("/api/auth/2fa"))!;
        Assert.True(status.Enabled);
        Assert.Equal(10, status.RecoveryCodesLeft);
        var replaced = await authed.PostAsJsonAsync("/api/auth/2fa/recovery-codes", new TwoFactorCodeRequest(Totp(setup.SharedKey)));
        replaced.EnsureSuccessStatusCode();
        var replacement = (await replaced.Content.ReadFromJsonAsync<RecoveryCodesResponse>())!;
        var newCodes = replacement.RecoveryCodes;
        await AssertFreshSessionAsync(app, client, replaced, replacement.Session);
        Assert.Equal(10, newCodes.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authed.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthFlowTests.CookiePost(client, "/api/auth/refresh", AuthFlowTests.Cookie(login))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login/2fa",
            new LoginTwoFactorRequest(challenge, Totp(setup.SharedKey)))).StatusCode);
        challenge = await Challenge();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login/2fa",
            new LoginTwoFactorRequest(challenge, RecoveryCode: oldCodes[0]))).StatusCode);
        var recovery = await client.PostAsJsonAsync("/api/auth/login/2fa", new LoginTwoFactorRequest(challenge, RecoveryCode: newCodes[0]));
        recovery.EnsureSuccessStatusCode();
        var recovered = (await recovery.Content.ReadFromJsonAsync<AuthResponse>())!;
        using var recoveredClient = app.ClientWith(recovered.AccessToken);
        Assert.Equal(9, (await recoveredClient.GetFromJsonAsync<TwoFactorStatus>("/api/auth/2fa"))!.RecoveryCodesLeft);
        var disable = await recoveredClient.PostAsJsonAsync("/api/auth/2fa/disable",
            new DisableTwoFactorRequest("Espresso4Boss", Totp(setup.SharedKey)));
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        var disabled = (await disable.Content.ReadFromJsonAsync<TwoFactorSessionResponse>())!;
        Assert.False(disabled.Session.User.TwoFactorEnabled);
        await AssertFreshSessionAsync(app, client, disable, disabled.Session);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthFlowTests.CookiePost(client, "/api/auth/refresh", AuthFlowTests.Cookie(recovery))).StatusCode);
        using var replacedClient = app.ClientWith(replacement.Session.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replacedClient.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await recoveredClient.GetAsync("/api/auth/me")).StatusCode);
        var plainSession = await app.LoginAsync(client, TestApp.OwnerEmail, "Espresso4Boss");
        Assert.False(plainSession.User.TwoFactorEnabled);
        Assert.False(plainSession.User.TwoFactorSetupRequired);
        using var scope = app.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditEvents.Select(e => e.Action).ToListAsync();
        foreach (var action in new[] { "2fa_enabled", "2fa_disabled", "2fa_recovery_codes_reset", "2fa_recovery_code_used", "login_2fa_failed" })
            Assert.Contains(action, events);
    }

    [Fact]
    public async Task Expired_and_tampered_challenges_are_rejected()
    {
        using var app = new TestApp();
        var (_, session) = await app.OwnerAsync();
        using var client = app.CreateClient();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == session.User.Id);
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Torvello.Login2fa.v1").ToTimeLimitedDataProtector();
        var payload = System.Text.Json.JsonSerializer.Serialize(new { UserId = user.Id, Stamp = user.SecurityStamp });
        var expired = protector.Protect(payload, DateTimeOffset.UtcNow.AddMinutes(-1));
        foreach (var token in new[] { expired, "tampered", "!" })
        {
            var response = await client.PostAsJsonAsync("/api/auth/login/2fa", new LoginTwoFactorRequest(token, "123456"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Cookie_requires_client_header_and_logout_clears_it_and_headers_cover_errors()
    {
        using var app = new TestApp();
        await app.OwnerAsync();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(TestApp.OwnerEmail, "Espresso4Boss"));
        var cookie = AuthFlowTests.Cookie(login);
        var rawCookie = login.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", rawCookie.ToLowerInvariant());
        Assert.Contains("secure", rawCookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", rawCookie.ToLowerInvariant());
        Assert.Contains("path=/api/auth", rawCookie);
        var body = await login.Content.ReadAsStringAsync();
        Assert.DoesNotContain("refreshToken", body);
        var missing = await AuthFlowTests.CookiePost(client, "/api/auth/refresh", cookie, false);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AuthFlowTests.CookiePost(client, "/api/auth/logout", cookie, false)).StatusCode);
        var logout = await AuthFlowTests.CookiePost(client, "/api/auth/logout", cookie);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.StartsWith("torvello_rt=;", logout.Headers.GetValues("Set-Cookie").Single());
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthFlowTests.CookiePost(client, "/api/auth/refresh", cookie)).StatusCode);
        foreach (var response in new[] { login, missing, logout, await client.GetAsync("/missing") })
        {
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("default-src 'none'; frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("camera=(), microphone=(), geolocation=()", response.Headers.GetValues("Permissions-Policy").Single());
            Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
            Assert.False(response.Headers.Contains("Server"));
        }
    }

    [Fact]
    public async Task Enrollment_login_recovery_and_lockout_follow_policy()
    {
        using var app = new TestApp { RequireTwoFactor = true };
        var (owner, session) = await app.OwnerAsync();
        Assert.True(session.User.TwoFactorSetupRequired);
        var blocked = await owner.GetAsync("/api/staff");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("Set up two-step sign-in first", await blocked.Content.ReadAsStringAsync());
        var setup = (await (await owner.PostAsync("/api/auth/2fa/setup", null)).Content.ReadFromJsonAsync<TwoFactorSetup>())!;
        Assert.StartsWith("otpauth://totp/Torvello:", setup.OtpauthUri);
        var enabled = await owner.PostAsJsonAsync("/api/auth/2fa/enable", new TwoFactorCodeRequest(Totp(setup.SharedKey)));
        enabled.EnsureSuccessStatusCode();
        var enrollment = (await enabled.Content.ReadFromJsonAsync<RecoveryCodesResponse>())!;
        var codes = enrollment.RecoveryCodes;
        Assert.Equal(10, codes.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/auth/me")).StatusCode);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await AssertFreshSessionAsync(app, client, enabled, enrollment.Session);
        async Task<TwoFactorChallenge> Challenge()
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(TestApp.OwnerEmail, "Espresso4Boss"));
            response.EnsureSuccessStatusCode();
            Assert.False(response.Headers.Contains("Set-Cookie"));
            var challenge = (await response.Content.ReadFromJsonAsync<TwoFactorChallenge>())!;
            Assert.True(challenge.TwoFactorRequired);
            return challenge;
        }
        var challenge = await Challenge();
        var code = Totp(setup.SharedKey);
        var signedIn = await client.PostAsJsonAsync("/api/auth/login/2fa",
            new LoginTwoFactorRequest(challenge.TwoFactorToken, code[..3] + " -" + code[3..]));
        signedIn.EnsureSuccessStatusCode();
        Assert.NotEmpty(AuthFlowTests.Cookie(signedIn));
        var authenticated = (await signedIn.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.True(authenticated.User.TwoFactorEnabled);
        Assert.False(authenticated.User.TwoFactorSetupRequired);
        using var authed = app.ClientWith(authenticated.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/staff")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await authed.PostAsJsonAsync("/api/auth/2fa/disable",
            new DisableTwoFactorRequest("Espresso4Boss", Totp(setup.SharedKey)))).StatusCode);

        var invite = await authed.PostAsJsonAsync("/api/staff", new InviteStaffRequest("manager@test.example", "Manager", Roles.Manager));
        invite.EnsureSuccessStatusCode();
        var accepted = await client.PostAsJsonAsync("/api/auth/accept-invite",
            new AcceptInviteRequest("manager@test.example", app.Mail.LatestTokenFor("manager@test.example"), "Mocha7788x"));
        accepted.EnsureSuccessStatusCode();
        var managerSession = (await accepted.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.True(managerSession.User.TwoFactorSetupRequired);
        using var manager = app.ClientWith(managerSession.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/staff")).StatusCode);
        var managerBlocked = await manager.GetAsync("/api/hr/policies");
        Assert.Equal(HttpStatusCode.Forbidden, managerBlocked.StatusCode);
        Assert.Contains("Set up two-step sign-in first", await managerBlocked.Content.ReadAsStringAsync());

        challenge = await Challenge();
        var recovery = await client.PostAsJsonAsync("/api/auth/login/2fa",
            new LoginTwoFactorRequest(challenge.TwoFactorToken, RecoveryCode: codes[0].Replace("-", " ")));
        recovery.EnsureSuccessStatusCode();
        var replay = await client.PostAsJsonAsync("/api/auth/login/2fa",
            new LoginTwoFactorRequest(challenge.TwoFactorToken, RecoveryCode: codes[0]));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        for (var i = 0; i < 4; i++)
        {
            challenge = await Challenge(); // Correct password must not reset failed second factors.
            var wrong = await client.PostAsJsonAsync("/api/auth/login/2fa",
                new LoginTwoFactorRequest(challenge.TwoFactorToken, "invalid"));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login/2fa",
            new LoginTwoFactorRequest(challenge.TwoFactorToken, Totp(setup.SharedKey)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(TestApp.OwnerEmail, "Espresso4Boss"))).StatusCode);
    }

    [Fact]
    public async Task Enable_keeps_caller_signed_in_and_rejects_previous_sessions()
    {
        using var app = new TestApp { RequireTwoFactor = true };
        var (owner, _) = await app.OwnerAsync();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(TestApp.OwnerEmail, "Espresso4Boss"));
        login.EnsureSuccessStatusCode();
        var previous = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;
        using var caller = app.ClientWith(previous.AccessToken);
        var setup = (await (await caller.PostAsync("/api/auth/2fa/setup", null)).Content.ReadFromJsonAsync<TwoFactorSetup>())!;
        var enabled = await caller.PostAsJsonAsync("/api/auth/2fa/enable", new TwoFactorCodeRequest(Totp(setup.SharedKey)));
        enabled.EnsureSuccessStatusCode();
        var result = (await enabled.Content.ReadFromJsonAsync<RecoveryCodesResponse>())!;
        Assert.Equal(10, result.RecoveryCodes.Length);
        Assert.True(result.Session.User.TwoFactorEnabled);
        Assert.False(result.Session.User.TwoFactorSetupRequired);
        Assert.NotEqual(AuthFlowTests.Cookie(login), AuthFlowTests.Cookie(enabled));
        Assert.Equal(HttpStatusCode.Unauthorized, (await caller.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AuthFlowTests.CookiePost(client, "/api/auth/refresh", AuthFlowTests.Cookie(login))).StatusCode);
        await AssertFreshSessionAsync(app, client, enabled, result.Session);
    }

    private static async Task AssertFreshSessionAsync(TestApp app, HttpClient client, HttpResponseMessage response, AuthResponse session)
    {
        using var authenticated = app.ClientWith(session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authenticated.GetAsync("/api/staff")).StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.Contains("path=/api/auth", cookie);
        Assert.Contains("expires=", cookie.ToLowerInvariant());
        Assert.DoesNotContain("refreshToken", await response.Content.ReadAsStringAsync());
        var refresh = await AuthFlowTests.CookiePost(client, "/api/auth/refresh", AuthFlowTests.Cookie(response));
        refresh.EnsureSuccessStatusCode();
        Assert.NotEqual(AuthFlowTests.Cookie(response), AuthFlowTests.Cookie(refresh));
        var refreshed = (await refresh.Content.ReadFromJsonAsync<AuthResponse>())!;
        using var refreshedClient = app.ClientWith(refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await refreshedClient.GetAsync("/api/staff")).StatusCode);
    }

    // RFC 6238: decode base32, use an eight-byte big-endian 30-second counter,
    // HMAC-SHA1 dynamic truncation, and six decimal digits.
    internal static string Totp(string sharedKey)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in sharedKey.Replace(" ", "").ToUpperInvariant().TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character);
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
        }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        var number = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (number % 1_000_000).ToString("D6");
    }
}
