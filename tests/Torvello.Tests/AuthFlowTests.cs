using System.Net;
using System.Net.Http.Json;
using Torvello.Application.Auth;
using Torvello.Application.Staff;
using Torvello.Domain;

namespace Torvello.Tests;

public sealed class AuthFlowTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Bootstrap_owner_must_change_password_before_anything_else()
    {
        var client = _app.CreateClient();
        var session = await _app.LoginAsync(client, TestApp.OwnerEmail, TestApp.OwnerBootstrapPassword);
        Assert.True(session.User.MustChangePassword);
        Assert.Equal(Roles.Owner, session.User.Role);

        var authed = _app.ClientWith(session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await authed.GetAsync("/api/staff")).StatusCode);

        var weak = await authed.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(TestApp.OwnerBootstrapPassword, "short1"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var (owner, _) = await _app.OwnerAsync();
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/staff")).StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_get_the_same_answer_and_lock_the_account()
    {
        await _app.OwnerAsync("Espresso4Boss");
        var client = _app.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody@torvello.test", "Whatever123"));
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(TestApp.OwnerEmail, "Whatever123"));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await Title(unknown), await Title(wrong));

        for (var i = 0; i < 4; i++) await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(TestApp.OwnerEmail, "Whatever123"));
        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(TestApp.OwnerEmail, "Espresso4Boss"));
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("Too many", await Title(locked));
    }

    [Fact]
    public async Task Invited_cashier_sets_password_by_email_and_cannot_manage_staff()
    {
        var (owner, _) = await _app.OwnerAsync();
        const string email = "rahim@torvello.test";

        var invite = await owner.PostAsJsonAsync("/api/staff", new InviteStaffRequest(email, "Rahim", Roles.Cashier));
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var duplicate = await owner.PostAsJsonAsync("/api/staff", new InviteStaffRequest(email.ToUpperInvariant(), "Copy", Roles.Cashier));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var client = _app.CreateClient();
        var beforeAccept = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Latte2026x"));
        Assert.Equal(HttpStatusCode.Unauthorized, beforeAccept.StatusCode);

        var token = _app.Mail.LatestTokenFor(email);
        var badPassword = await client.PostAsJsonAsync("/api/auth/accept-invite", new AcceptInviteRequest(email, token, "rahim12345"));
        Assert.Equal(HttpStatusCode.BadRequest, badPassword.StatusCode);
        var accepted = await client.PostAsJsonAsync("/api/auth/accept-invite", new AcceptInviteRequest(email, token, "Latte2026x"));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        var reused = await client.PostAsJsonAsync("/api/auth/accept-invite", new AcceptInviteRequest(email, token, "Latte2026y"));
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);

        var session = await _app.LoginAsync(client, email, "Latte2026x");
        Assert.Equal(Roles.Cashier, session.User.Role);
        Assert.False(session.User.MustChangePassword);
        var cashier = _app.ClientWith(session.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/staff")).StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_ends_the_session()
    {
        await _app.OwnerAsync("Espresso4Boss");
        var client = _app.CreateClient();
        var first = await _app.LoginAsync(client, TestApp.OwnerEmail, "Espresso4Boss");

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = (await refreshed.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // Someone replays the old token: both it and the newer one stop working.
        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var afterReplay = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
    }

    [Fact]
    public async Task Forgot_password_emails_a_reset_link_and_signs_out_other_devices()
    {
        await _app.OwnerAsync("Espresso4Boss");
        var client = _app.CreateClient();
        var oldSession = await _app.LoginAsync(client, TestApp.OwnerEmail, "Espresso4Boss");

        var unknown = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest("nobody@torvello.test"));
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.DoesNotContain(_app.Mail.Sent, m => m.To == "nobody@torvello.test");

        var forgot = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(TestApp.OwnerEmail));
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        var token = _app.Mail.LatestTokenFor(TestApp.OwnerEmail);

        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(TestApp.OwnerEmail, token, "Cappuccino9"));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var again = await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(TestApp.OwnerEmail, token, "Cappuccino10"));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.ClientWith(oldSession.AccessToken).GetAsync("/api/auth/me")).StatusCode);
        var oldRefresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(oldSession.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
        await _app.LoginAsync(client, TestApp.OwnerEmail, "Cappuccino9");
    }

    [Fact]
    public async Task Disabling_staff_cuts_off_their_access_immediately()
    {
        var (owner, ownerSession) = await _app.OwnerAsync();
        const string email = "karim@torvello.test";
        var invite = await owner.PostAsJsonAsync("/api/staff", new InviteStaffRequest(email, "Karim", Roles.Manager));
        var staff = (await invite.Content.ReadFromJsonAsync<UserDto>())!;
        var client = _app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/accept-invite", new AcceptInviteRequest(email, _app.Mail.LatestTokenFor(email), "Mocha7788x"));
        var session = await _app.LoginAsync(client, email, "Mocha7788x");
        var manager = _app.ClientWith(session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/auth/me")).StatusCode);

        var disable = await owner.PutAsJsonAsync($"/api/staff/{staff.Id}", new UpdateStaffRequest("Karim", Roles.Manager, IsActive: false));
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await manager.GetAsync("/api/auth/me")).StatusCode);
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        var removeSelf = await owner.PutAsJsonAsync($"/api/staff/{ownerSession.User.Id}", new UpdateStaffRequest("Owner", Roles.Manager, IsActive: true));
        Assert.Equal(HttpStatusCode.BadRequest, removeSelf.StatusCode);
    }

    private static async Task<string> Title(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemBody>())?.Title ?? "";

    private sealed record ProblemBody(string? Title);
}
