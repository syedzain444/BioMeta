using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BiometricDashboard.Infrastructure.Services;

public sealed class DatabaseSeeder(IServiceScopeFactory scopeFactory, ILogger<DatabaseSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var security = scope.ServiceProvider.GetRequiredService<ISecurityRepository>();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

            var admin = await security.GetUserByEmployeeIdAsync("ADMIN", cancellationToken);
            if (admin is not null && admin.PasswordHash.Contains("PLACEHOLDER", StringComparison.Ordinal))
            {
                var hash = auth.HashPassword("Admin@123");
                await security.EnsureAdminPasswordAsync("ADMIN", hash, admin.UserId, cancellationToken);
                logger.LogWarning("Default admin password set to Admin@123 — change after first login.");
            }

            if (!await security.AnyUserExistsAsync(cancellationToken))
            {
                var hash = auth.HashPassword("Admin@123");
                var user = new SecUser
                {
                    EmployeeId = "ADMIN",
                    DisplayName = "Portal Administrator",
                    PasswordHash = hash,
                    IsActive = true,
                    MustChangePassword = true,
                };
                var roles = await security.ListRolesAsync(cancellationToken);
                var super = roles.FirstOrDefault(r => r.RoleCode == "SUPER_ADMIN");
                var roleIds = super is not null ? new[] { super.RoleId } : Array.Empty<long>();
                await security.CreateUserAsync(user, roleIds, 1, cancellationToken);
                logger.LogWarning("Seeded ADMIN user with password Admin@123");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database seed skipped — run SQL script 002_gate_entry_portal_schema.sql on Oracle first.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
