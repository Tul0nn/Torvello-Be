using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Torvello.Application.Auth;
using Torvello.Application.Common;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Tests;

/// <summary>Runs the real API in memory with SQLite and a captured mailbox.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string OwnerEmail = "owner@torvello.test";
    public const string OwnerBootstrapPassword = "bootstrap-pass-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    public CapturingEmailSender Mail { get; } = new();

    public TestApp() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Initialize", "EnsureCreated");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-1234567890");
        builder.UseSetting("Bootstrap:OwnerEmail", OwnerEmail);
        builder.UseSetting("Bootstrap:OwnerPassword", OwnerBootstrapPassword);
        builder.UseSetting("RateLimiting:AuthPermitPerMinute", "1000");
        builder.UseSetting("AppLinks:WebBaseUrl", "https://app.test");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Mail);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }

    public async Task<AuthResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>Signs in the bootstrap owner and replaces the temporary password.</summary>
    public async Task<(HttpClient Client, AuthResponse Session)> OwnerAsync(string newPassword = "Espresso4Boss")
    {
        var client = CreateClient();
        var first = await LoginAsync(client, OwnerEmail, OwnerBootstrapPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.AccessToken);
        var changed = await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(OwnerBootstrapPassword, newPassword));
        changed.EnsureSuccessStatusCode();
        var session = (await changed.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return (client, session);
    }

    /// <summary>Invites a staff member, accepts the invite, signs them in and returns their client.</summary>
    public async Task<HttpClient> StaffAsync(HttpClient owner, string email, string role, string password = "Mocha7788x")
    {
        var invite = await owner.PostAsJsonAsync("/api/staff", new Application.Staff.InviteStaffRequest(email, email.Split('@')[0], role));
        invite.EnsureSuccessStatusCode();
        var client = CreateClient();
        var accepted = await client.PostAsJsonAsync("/api/auth/accept-invite", new AcceptInviteRequest(email, Mail.LatestTokenFor(email), password));
        accepted.EnsureSuccessStatusCode();
        return ClientWith((await LoginAsync(client, email, password)).AccessToken);
    }

    public HttpClient ClientWith(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}

public sealed partial class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>The token from the newest link sent to this address.</summary>
    public string LatestTokenFor(string email)
    {
        var message = Sent.Last(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase));
        return TokenPattern().Match(message.TextBody).Groups[1].Value;
    }

    [GeneratedRegex(@"[?&]token=([A-Za-z0-9_\-]+)")]
    private static partial Regex TokenPattern();
}
