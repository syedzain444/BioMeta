using System.Collections.Concurrent;
using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BiometricDashboard.Infrastructure.Services;

public sealed class EmployeeSyncJobService(IServiceScopeFactory scopeFactory) : IEmployeeSyncJobService
{
    private readonly ConcurrentDictionary<Guid, EmployeeSyncJob> _jobs = new();

    public EmployeeSyncJob CreateJob(
        long deviceId,
        string deviceIp,
        string deviceName,
        IReadOnlyList<EmployeeImportRow> rows,
        string jobKind = "upload")
    {
        var valid = rows.Where(r => r.IsValid).ToList();
        var job = new EmployeeSyncJob
        {
            JobId = Guid.NewGuid(),
            JobKind = jobKind,
            DeviceId = deviceId,
            DeviceIp = deviceIp,
            DeviceName = deviceName,
            State = EmployeeSyncJobState.Queued,
            Total = valid.Count,
            LineResults = [],
        };
        _jobs[job.JobId] = job;
        TrimOldJobs();
        return job;
    }

    public EmployeeSyncJob? GetJob(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public async Task<EmployeeSyncJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(jobId, out var cached))
            return cached;
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEmployeeSyncJobRepository>();
        var fromDb = await repository.GetJobAsync(jobId, cancellationToken);
        if (fromDb is not null)
            _jobs[jobId] = fromDb;
        return fromDb;
    }

    public void UpdateJob(EmployeeSyncJob job)
    {
        job.UpdatedUtc = DateTime.UtcNow;
        _jobs[job.JobId] = job;
    }

    public async Task SaveJobAsync(
        EmployeeSyncJob job,
        long? actorUserId,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        job.UpdatedUtc = DateTime.UtcNow;
        _jobs[job.JobId] = job;
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEmployeeSyncJobRepository>();
        var notifier = scope.ServiceProvider.GetRequiredService<IEmployeeSyncNotifier>();
        if (isNew)
            await repository.InsertJobAsync(job, actorUserId, cancellationToken);
        else
            await repository.UpdateJobAsync(job, cancellationToken);
        if (job.LineResults.Count > 0)
            await repository.ReplaceLinesAsync(job.JobId, job.LineResults, cancellationToken);
        await notifier.PublishJobUpdateAsync(job, cancellationToken);
    }

    public void CancelJob(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        job.State = EmployeeSyncJobState.Cancelled;
        job.LogLine = "Cancelled by operator.";
        job.UpdatedUtc = DateTime.UtcNow;
    }

    private void TrimOldJobs()
    {
        if (_jobs.Count <= 120) return;
        var drop = _jobs.Values
            .OrderBy(j => j.CreatedUtc)
            .Take(_jobs.Count - 90)
            .Select(j => j.JobId)
            .ToList();
        foreach (var id in drop)
            _jobs.TryRemove(id, out _);
    }
}