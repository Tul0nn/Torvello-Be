using System.Data.Common;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Torvello.Api.Infrastructure;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Tests;

public sealed class DeploymentTests
{
    [Theory]
    [InlineData("KnownProxies", "10.20.0.2", "10.20.0.2", true)]
    [InlineData("KnownNetworks", "10.20.0.0/24", "10.20.0.2", true)]
    [InlineData("KnownProxies", "10.20.0.2", "10.21.0.2", false)]
    [InlineData("KnownNetworks", "10.20.0.0/24", "10.21.0.2", false)]
    [InlineData("KnownProxies", "10.20.0.2", "127.0.0.1", false)]
    [InlineData(null, null, "127.0.0.1", false)]
    public async Task Forwarding_controls_https_and_rate_limit_partition(string? kind, string? value, string peer, bool trusted)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(kind is null ? [] :
            new Dictionary<string, string?> { [$"ForwardedHeaders:{kind}:0"] = value }).Build();
        var options = new ForwardedHeadersOptions();
        ProxyForwarding.Configure(options, config);
        Assert.Equal(1, options.ForwardLimit);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = 429;
            limits.AddPolicy("probe", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress!.ToString(),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) }));
        });
        await using var app = builder.Build();
        if (options.ForwardedHeaders != ForwardedHeaders.None) app.UseForwardedHeaders(options);
        app.UseRouting();
        app.UseRateLimiter();
        app.MapGet("/", (HttpContext context) =>
        {
            context.Response.Headers["Observed-Https"] = context.Request.IsHttps.ToString();
            context.Response.Headers["Observed-Ip"] = context.Connection.RemoteIpAddress!.ToString();
        }).RequireRateLimiting("probe");
        await app.StartAsync();
        var server = app.GetTestServer();

        async Task<HttpContext> Send(string client) => await server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Scheme = "http";
            context.Request.Method = "GET";
            context.Request.Path = "/";
            context.Request.Headers["X-Forwarded-For"] = client;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        var first = await Send("203.0.113.1");
        Assert.Equal(200, first.Response.StatusCode);
        Assert.Equal(trusted.ToString(), first.Response.Headers["Observed-Https"].ToString());
        Assert.Equal(trusted ? "203.0.113.1" : peer, first.Response.Headers["Observed-Ip"].ToString());
        Assert.Equal(429, (await Send("203.0.113.1")).Response.StatusCode);
        Assert.Equal(trusted ? 200 : 429, (await Send("203.0.113.2")).Response.StatusCode);
    }

    [Theory]
    [InlineData("KnownProxies", "not-an-ip")]
    [InlineData("KnownProxies", "")]
    [InlineData("KnownNetworks", "10.0.0.0/99")]
    [InlineData("KnownNetworks", "")]
    public void Invalid_proxy_configuration_has_clear_error(string kind, string value)
    {
        var key = $"ForwardedHeaders:{kind}:0";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => ProxyForwarding.Configure(new ForwardedHeadersOptions(), config));
        Assert.Contains(key, error.Message);
    }

    [Fact]
    public void Invalid_proxy_configuration_prevents_api_startup()
    {
        using var app = new TestApp();
        using var invalid = app.WithWebHostBuilder(builder =>
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", "invalid-cidr"));
        var error = Assert.Throws<InvalidOperationException>(() => invalid.CreateClient());
        Assert.Contains("ForwardedHeaders:KnownNetworks:0", error.Message);
    }

    [Fact]
    public async Task Health_is_anonymous_json_without_rate_limit()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        for (var i = 0; i < 65; i++)
        {
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Health_remains_available_during_owner_account_setup()
    {
        using var app = new TestApp { RequireTwoFactor = true };
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var session = await app.LoginAsync(client, TestApp.OwnerEmail, TestApp.OwnerBootstrapPassword);
        using var owner = app.ClientWith(session.AccessToken);
        using var response = await owner.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_returns_503_without_details_when_database_check_fails()
    {
        using var app = new TestApp();
        using var failed = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new FailedHealthCheck()))));
        using var client = failed.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    private sealed class FailedHealthCheck : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText == "SELECT 1") throw new InvalidOperationException("private database detail");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
