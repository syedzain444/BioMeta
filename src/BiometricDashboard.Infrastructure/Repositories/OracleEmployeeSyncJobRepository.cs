using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Infrastructure.Persistence;
using Dapper;

namespace BiometricDashboard.Infrastructure.Repositories;

public sealed class OracleEmployeeSyncJobRepository(OracleConnectionFactory factory) : IEmployeeSyncJobRepository
{
    public async Task InsertJobAsync(EmployeeSyncJob job, long? actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(
            """
            INSERT INTO BIO_EMPLOYEE_SYNC_JOB
              (JOB_ID, JOB_KIND, BIO_DEVICE_ID, DEVICE_NAME, DEVICE_IP, STATE, TOTAL_COUNT, UPLOADED_COUNT, FAILED_COUNT,
               VERIFIED_COUNT, DELETED_COUNT, LOG_LINE, LAST_ERROR, CREATED_BY)
            VALUES
              (:JobId, :JobKind, :DeviceId, :DeviceName, :DeviceIp, :State, :Total, :Uploaded, :Failed, :Verified, :Deleted,
               :LogLine, :LastError, :Actor)
            """,
            MapJob(job, actorUserId));
    }

    public async Task UpdateJobAsync(EmployeeSyncJob job, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(
            """
            UPDATE BIO_EMPLOYEE_SYNC_JOB SET
              STATE = :State,
              TOTAL_COUNT = :Total,
              UPLOADED_COUNT = :Uploaded,
              FAILED_COUNT = :Failed,
              VERIFIED_COUNT = :Verified,
              DELETED_COUNT = :Deleted,
              LOG_LINE = :LogLine,
              LAST_ERROR = :LastError,
              UPDATED_AT = SYSTIMESTAMP,
              COMPLETED_AT = CASE WHEN :State IN ('Completed','Failed','Cancelled') THEN SYSTIMESTAMP ELSE COMPLETED_AT END
            WHERE JOB_ID = :JobId
            """,
            MapJob(job, null));
    }

    public async Task ReplaceLinesAsync(Guid jobId, IReadOnlyList<EmployeeSyncLineResult> lines, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync("DELETE FROM BIO_EMPLOYEE_SYNC_LINE WHERE JOB_ID = :JobId", new { JobId = jobId.ToString() });
        var order = 0;
        foreach (var line in lines)
        {
            order++;
            await conn.ExecuteAsync(
                """
                INSERT INTO BIO_EMPLOYEE_SYNC_LINE
                  (JOB_ID, EMPLOYEE_ID, DISPLAY_NAME, SUCCEEDED, VERIFIED, METHOD, ERROR_MSG, SORT_ORDER)
                VALUES
                  (:JobId, :EmployeeId, :Name, :Succeeded, :Verified, :Method, :Error, :SortOrder)
                """,
                new
                {
                    JobId = jobId.ToString(),
                    line.EmployeeId,
                    Name = line.Name,
                    Succeeded = line.Succeeded ? "Y" : "N",
                    Verified = line.Verified ? "Y" : "N",
                    Method = line.Method,
                    Error = TruncateDb(line.Error, 1900),
                    SortOrder = order,
                });
        }
    }

    public async Task<EmployeeSyncJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var row = await conn.QueryFirstOrDefaultAsync<JobRow>(
            """
            SELECT JOB_ID JobId, JOB_KIND JobKind, BIO_DEVICE_ID DeviceId, DEVICE_NAME DeviceName, DEVICE_IP DeviceIp,
                   STATE State, TOTAL_COUNT Total, UPLOADED_COUNT Uploaded, FAILED_COUNT Failed, VERIFIED_COUNT Verified,
                   DELETED_COUNT Deleted, LOG_LINE LogLine, LAST_ERROR LastError
            FROM BIO_EMPLOYEE_SYNC_JOB WHERE JOB_ID = :JobId
            """,
            new { JobId = jobId.ToString() });
        if (row is null) return null;
        var lines = (await conn.QueryAsync<LineRow>(
            """
            SELECT EMPLOYEE_ID EmployeeId, DISPLAY_NAME Name, SUCCEEDED Succeeded, VERIFIED Verified, METHOD Method, ERROR_MSG Error
            FROM BIO_EMPLOYEE_SYNC_LINE WHERE JOB_ID = :JobId ORDER BY SORT_ORDER
            """,
            new { JobId = jobId.ToString() })).ToList();
        return Map(row, lines);
    }

    public async Task<IReadOnlyList<EmployeeSyncJob>> ListRecentAsync(long? deviceId, int limit, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        limit = Math.Clamp(limit, 1, 100);
        var sql = """
            SELECT * FROM (
              SELECT JOB_ID JobId, JOB_KIND JobKind, BIO_DEVICE_ID DeviceId, DEVICE_NAME DeviceName, DEVICE_IP DeviceIp,
                     STATE State, TOTAL_COUNT Total, UPLOADED_COUNT Uploaded, FAILED_COUNT Failed, VERIFIED_COUNT Verified,
                     DELETED_COUNT Deleted, LOG_LINE LogLine, LAST_ERROR LastError
              FROM BIO_EMPLOYEE_SYNC_JOB
              WHERE (:DeviceId IS NULL OR BIO_DEVICE_ID = :DeviceId)
              ORDER BY CREATED_AT DESC
            ) WHERE ROWNUM <= :Limit
            """;
        var rows = await conn.QueryAsync<JobRow>(sql, new { DeviceId = deviceId, Limit = limit });
        return rows.Select(r => Map(r, [])).ToList();
    }

    private static object MapJob(EmployeeSyncJob job, long? actor) => new
    {
        JobId = job.JobId.ToString(),
        job.JobKind,
        DeviceId = job.DeviceId,
        job.DeviceName,
        DeviceIp = job.DeviceIp,
        State = job.State.ToString(),
        Total = job.Total,
        Uploaded = job.Uploaded,
        Failed = job.Failed,
        Verified = job.Verified,
        Deleted = job.Deleted,
        LogLine = job.LogLine,
        LastError = TruncateDb(job.LastError, 1900),
        Actor = actor,
    };

    private static EmployeeSyncJob Map(JobRow row, List<LineRow> lines) => new()
    {
        JobId = Guid.Parse(row.JobId),
        JobKind = row.JobKind ?? "upload",
        DeviceId = row.DeviceId,
        DeviceName = row.DeviceName ?? "",
        DeviceIp = row.DeviceIp ?? "",
        State = Enum.TryParse<EmployeeSyncJobState>(row.State, out var st) ? st : EmployeeSyncJobState.Failed,
        Total = row.Total,
        Uploaded = row.Uploaded,
        Failed = row.Failed,
        Verified = row.Verified,
        Deleted = row.Deleted,
        LogLine = row.LogLine,
        LastError = row.LastError,
        LineResults = lines.Select(l => new EmployeeSyncLineResult
        {
            EmployeeId = l.EmployeeId,
            Name = l.Name,
            Succeeded = l.Succeeded == "Y",
            Verified = l.Verified == "Y",
            Method = l.Method ?? "",
            Error = l.Error,
        }).ToList(),
    };

    private sealed class JobRow
    {
        public string JobId { get; set; } = "";
        public string? JobKind { get; set; }
        public long DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? DeviceIp { get; set; }
        public string State { get; set; } = "";
        public int Total { get; set; }
        public int Uploaded { get; set; }
        public int Failed { get; set; }
        public int Verified { get; set; }
        public int Deleted { get; set; }
        public string? LogLine { get; set; }
        public string? LastError { get; set; }
    }

    private sealed class LineRow
    {
        public string EmployeeId { get; set; } = "";
        public string? Name { get; set; }
        public string Succeeded { get; set; } = "N";
        public string Verified { get; set; } = "N";
        public string? Method { get; set; }
        public string? Error { get; set; }
    }

    private static string? TruncateDb(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLen) return value;
        return value[..maxLen];
    }
}