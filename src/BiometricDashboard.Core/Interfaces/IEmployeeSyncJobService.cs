using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public interface IEmployeeSyncJobService
{
    EmployeeSyncJob CreateJob(long deviceId, string deviceIp, string deviceName, IReadOnlyList<EmployeeImportRow> rows, string jobKind = "upload");
    EmployeeSyncJob? GetJob(Guid jobId);
    Task<EmployeeSyncJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    void UpdateJob(EmployeeSyncJob job);
    Task SaveJobAsync(EmployeeSyncJob job, long? actorUserId, bool isNew, CancellationToken cancellationToken = default);
    void CancelJob(Guid jobId);
}