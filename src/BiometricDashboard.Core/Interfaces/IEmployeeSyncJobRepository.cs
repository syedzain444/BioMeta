using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public interface IEmployeeSyncJobRepository
{
    Task InsertJobAsync(EmployeeSyncJob job, long? actorUserId, CancellationToken cancellationToken = default);
    Task UpdateJobAsync(EmployeeSyncJob job, CancellationToken cancellationToken = default);
    Task ReplaceLinesAsync(Guid jobId, IReadOnlyList<EmployeeSyncLineResult> lines, CancellationToken cancellationToken = default);
    Task<EmployeeSyncJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmployeeSyncJob>> ListRecentAsync(long? deviceId, int limit, CancellationToken cancellationToken = default);
}