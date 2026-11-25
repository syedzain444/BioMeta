using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

/// <summary>Real-time sync job progress (SignalR implementation in Web layer).</summary>
public interface IEmployeeSyncNotifier
{
    Task PublishJobUpdateAsync(EmployeeSyncJob job, CancellationToken cancellationToken = default);
}