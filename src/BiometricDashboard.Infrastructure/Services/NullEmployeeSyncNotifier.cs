using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;

namespace BiometricDashboard.Infrastructure.Services;

public sealed class NullEmployeeSyncNotifier : IEmployeeSyncNotifier
{
    public Task PublishJobUpdateAsync(EmployeeSyncJob job, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}