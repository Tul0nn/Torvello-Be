using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Torvello.Application.Auth;
using Torvello.Application.Common;
using Torvello.Application.Staff;
using Torvello.Domain;
using Torvello.Infrastructure.Auth;
using Torvello.Infrastructure.Email;
using Torvello.Infrastructure.Persistence;
using Torvello.Infrastructure.Staff;
using Torvello.Infrastructure.Sync;
using Torvello.Application.Sync;
using Torvello.Application.Inventory;
using Torvello.Application.Hr;

namespace Torvello.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));
        services.Configure<AppLinkOptions>(configuration.GetSection(AppLinkOptions.Section));
        services.Configure<BootstrapOptions>(configuration.GetSection(BootstrapOptions.Section));

        var connectionString = configuration.GetConnectionString("Torvello");
        if (!string.IsNullOrEmpty(connectionString))
        {
            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
        }

        services.AddDataProtection();
        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;
                options.Password.RequiredLength = PasswordRules.MinLength;
                options.Password.RequireDigit = false; // PasswordRules covers letters + digits
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .AddPasswordValidator<TorvelloPasswordValidator>();

        // Invitation and reset links stay valid for 3 days.
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromDays(3));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TokenService>();
        services.AddScoped<AuditLog>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IStaffService, StaffService>();
        services.AddScoped<IDeviceService, DeviceService>();
        services.AddScoped<ISyncService, SyncService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IPolicyService, Hr.PolicyService>();
        services.AddScoped<IAttendanceService, Hr.AttendanceService>();
        services.AddScoped<ChangeFeed>();
        services.AddScoped<Application.People.ICustomerService, People.CustomerService>();
        services.AddScoped<Application.People.IEmployeeService, People.EmployeeService>();
        services.AddScoped<Application.People.IBillService, People.BillService>();

        if (string.Equals(configuration["Email:Provider"], "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, PickupEmailSender>();
        }

        return services;
    }
}
