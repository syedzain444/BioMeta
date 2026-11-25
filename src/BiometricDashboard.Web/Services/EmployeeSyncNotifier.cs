using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BiometricDashboard.Web.Services;

public sealed class EmployeeSyncNotifier(IHubContext<EmployeeSyncHub> hub) : IEmployeeSyncNotifier
{
    public Task PublishJobUpdateAsync(EmployeeSyncJob job, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(job.JobId.ToString()).SendAsync("SyncJobUpdated", ToPayload(job), cancellationToken);

    private static object ToPayload(EmployeeSyncJob job) => new
    {
        jobId = job.JobId,
        jobKind = job.JobKind,
        state = job.State.ToString(),
        job.DeviceId,
        job.DeviceName,
        total = job.Total,
        uploaded = job.Uploaded,
        failed = job.Failed,
        verified = job.Verified,
        deleted = job.Deleted,
        log = job.LogLine,
        error = job.LastError,
        updatedUtc = job.UpdatedUtc,
        lines = job.LineResults.Select(l => new
        {
            l.EmployeeId,
            l.Name,
            l.Succeeded,
            l.Verified,
            l.Method,
            error = l.Error,
        }),
    };
}