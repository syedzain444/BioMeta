using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Infrastructure.Device;
using BiometricDashboard.Infrastructure.Persistence;
using BiometricDashboard.Infrastructure.Repositories;
using BiometricDashboard.Infrastructure.Security;
using BiometricDashboard.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BiometricDashboard.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBiometricInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DeviceOptions>()
            .Bind(configuration.GetSection(DeviceOptions.SectionName))
            .PostConfigure(o =>
            {
                var section = configuration.GetSection(DeviceOptions.SectionName);
                var ports = section.GetSection("PushPorts").Get<int[]>();
                if (ports is { Length: >= 2 } && string.IsNullOrEmpty(section["CmdPushPort"]))
                {
                    o.CmdPushPort = ports[0];
                    o.HbpPushPort = ports[1];
                }

                o.PushPorts = [o.CmdPushPort, o.HbpPushPort];
            });
        services.TryAddSingleton<IDashboardLivePresence, DefaultDashboardLivePresence>();

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("GateEntryOracle")))
            throw new InvalidOperationException("ConnectionStrings:GateEntryOracle is required in appsettings.json");

        services.AddSingleton<OracleConnectionFactory>();
        services.AddScoped<IAttendanceRepository, OracleAttendanceRepository>();
        services.AddScoped<IAttendanceLogQuery, OracleAttendanceRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddSingleton<IEmployeePhotoCache, EmployeePhotoCache>();
        services.AddScoped<IEmployeeRegistryRepository, OracleEmployeeRegistryRepository>();
        services.AddScoped<IDeviceEnrollmentRepository, OracleDeviceEnrollmentRepository>();
        services.AddScoped<IEmployeeSyncJobRepository, OracleEmployeeSyncJobRepository>();
        services.AddScoped<ISecurityRepository, SecurityRepository>();
        services.AddSingleton<DeviceSecretProtector>();
        services.AddDataProtection();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();
        services.AddHostedService<DatabaseSeeder>();
        services.AddSingleton<DevicePushSyncCoordinator>();
        services.AddSingleton<DeviceEmployeeDownloadCoordinator>();
        services.AddSingleton<IEmployeeSyncNotifier, NullEmployeeSyncNotifier>();
        services.AddSingleton<IEmployeeSyncJobService, EmployeeSyncJobService>();
        services.AddSingleton<IDeviceClient, HanwangDeviceClient>();

        services.AddSingleton<DeviceSyncBackgroundService>();
        services.AddSingleton<IDeviceSyncService>(sp => sp.GetRequiredService<DeviceSyncBackgroundService>());
        services.AddHostedService(sp => sp.GetRequiredService<DeviceSyncBackgroundService>());
        services.AddHostedService<HanwangPushServerHostedService>();

        return services;
    }
}
