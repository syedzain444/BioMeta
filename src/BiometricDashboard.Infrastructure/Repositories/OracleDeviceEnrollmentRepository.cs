using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Utilities;
using BiometricDashboard.Infrastructure.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace BiometricDashboard.Infrastructure.Repositories;

public sealed class OracleDeviceEnrollmentRepository(
    OracleConnectionFactory factory,
    ILogger<OracleDeviceEnrollmentRepository> logger) : IDeviceEnrollmentRepository
{
    public async Task<Guid> SaveFetchBatchAsync(
        int deviceCount,
        int rowCount,
        long? actorUserId,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var batchId = Guid.NewGuid();
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        try
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO BIO_DEVICE_ENROLLMENT_BATCH (BATCH_ID, DEVICE_COUNT, ROW_COUNT, CREATED_BY, NOTE_TEXT)
                VALUES (:BatchId, :DeviceCount, :RowCount, :Actor, :Note)
                """,
                new
                {
                    BatchId = batchId.ToString("N"),
                    DeviceCount = deviceCount,
                    RowCount = rowCount,
                    Actor = actorUserId,
                    Note = note,
                });
        }
        catch (OracleException ex) when (ex.Number == 942)
        {
            logger.LogWarning("BIO_DEVICE_ENROLLMENT_BATCH missing — run 013_device_enrollment_hub.sql");
            return batchId;
        }

        return batchId;
    }

    public async Task<int> UpsertFromDevicePullAsync(
        Guid batchId,
        long bioDeviceId,
        IReadOnlyList<DeviceEmployeeRecord> employees,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (bioDeviceId <= 0 || employees.Count == 0) return 0;

        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var batchKey = batchId.ToString("N");
        var count = 0;

        foreach (var emp in employees)
        {
            var id = NicIdNormalizer.Normalize(emp.EmployeeId);
            if (string.IsNullOrEmpty(id)) continue;

            var enrolledAt = emp.EnrolledAtUtc;
            if (enrolledAt is { Kind: DateTimeKind.Utc })
                enrolledAt = enrolledAt.Value.ToLocalTime();

            var bio = EmployeeBiometricStatus.FromRecord(emp).ToString();
            try
            {
                await conn.ExecuteAsync(
                    """
                    MERGE INTO BIO_DEVICE_ENROLLMENT t
                    USING (SELECT :DeviceId BIO_DEVICE_ID, :EmployeeId EMPLOYEE_ID FROM DUAL) s
                    ON (t.BIO_DEVICE_ID = s.BIO_DEVICE_ID AND t.EMPLOYEE_ID = s.EMPLOYEE_ID AND t.DELETED_AT IS NULL)
                    WHEN MATCHED THEN UPDATE SET
                      DISPLAY_NAME = :DisplayName,
                      DEPARTMENT = :Department,
                      CARD_NUMBER = :CardNumber,
                      AUTHORITY = :Authority,
                      CHECK_TYPE = :CheckType,
                      FACE_TEMPLATE_CNT = :FaceCount,
                      BIOMETRIC_STATUS = :BiometricStatus,
                      ENROLLED_AT = :EnrolledAt,
                      ENROLLED_SOURCE = :EnrolledSource,
                      LAST_FETCHED_AT = SYSTIMESTAMP,
                      FETCH_BATCH_ID = :BatchId,
                      UPDATED_BY = :Actor,
                      UPDATED_AT = SYSTIMESTAMP
                    WHEN NOT MATCHED THEN INSERT
                      (BIO_DEVICE_ID, EMPLOYEE_ID, DISPLAY_NAME, DEPARTMENT, CARD_NUMBER, AUTHORITY, CHECK_TYPE,
                       FACE_TEMPLATE_CNT, BIOMETRIC_STATUS, ENROLLED_AT, ENROLLED_SOURCE, LAST_FETCHED_AT, FETCH_BATCH_ID, CREATED_BY, UPDATED_BY)
                    VALUES
                      (:DeviceId, :EmployeeId, :DisplayName, :Department, :CardNumber, :Authority, :CheckType,
                       :FaceCount, :BiometricStatus, :EnrolledAt, :EnrolledSource, SYSTIMESTAMP, :BatchId, :Actor, :Actor)
                    """,
                    new
                    {
                        DeviceId = bioDeviceId,
                        EmployeeId = id,
                        DisplayName = emp.Name,
                        Department = emp.Department,
                        CardNumber = emp.CardNumber,
                        Authority = emp.Authority,
                        CheckType = emp.CheckType,
                        FaceCount = emp.FaceTemplateCount,
                        BiometricStatus = bio,
                        EnrolledAt = enrolledAt,
                        EnrolledSource = emp.EnrolledAtSource,
                        BatchId = batchKey,
                        Actor = actorUserId,
                    });
                count++;
            }
            catch (OracleException ex) when (ex.Number == 942)
            {
                logger.LogWarning("BIO_DEVICE_ENROLLMENT missing — run 013_device_enrollment_hub.sql");
                return 0;
            }
        }

        return count;
    }

    public async Task<IReadOnlyList<DeviceEnrollmentRecord>> ListByDevicesAsync(
        IReadOnlyList<long> bioDeviceIds,
        CancellationToken cancellationToken = default)
    {
        if (bioDeviceIds.Count == 0) return [];
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);

        var ids = bioDeviceIds.Where(x => x > 0).Distinct().ToList();
        var placeholders = string.Join(", ", ids.Select((_, i) => $":Id{i}"));
        var p = new DynamicParameters();
        for (var i = 0; i < ids.Count; i++)
            p.Add($"Id{i}", ids[i]);

        try
        {
            var sql = $"""
                SELECT ENROLLMENT_ID EnrollmentId, BIO_DEVICE_ID BioDeviceId, EMPLOYEE_ID EmployeeId,
                       DISPLAY_NAME DisplayName, DEPARTMENT Department, CARD_NUMBER CardNumber, AUTHORITY Authority,
                       CHECK_TYPE CheckType, FACE_TEMPLATE_CNT FaceTemplateCount, BIOMETRIC_STATUS BiometricStatus,
                       ENROLLED_AT EnrolledAt, ENROLLED_SOURCE EnrolledSource, LAST_FETCHED_AT LastFetchedAt,
                       FETCH_BATCH_ID FetchBatchId
                FROM BIO_DEVICE_ENROLLMENT
                WHERE DELETED_AT IS NULL AND BIO_DEVICE_ID IN ({placeholders})
                ORDER BY BIO_DEVICE_ID, EMPLOYEE_ID
                """;
            var rows = await conn.QueryAsync<DeviceEnrollmentRecord>(sql, p);
            return rows.ToList();
        }
        catch (OracleException ex) when (ex.Number == 942)
        {
            logger.LogWarning("BIO_DEVICE_ENROLLMENT missing — run 013_device_enrollment_hub.sql");
            return [];
        }
    }

    public DeviceEnrollmentCompareResult CompareRows(IEnumerable<DeviceEnrollmentCompareRowInput> rows)
    {
        var list = rows
            .Select(r => new DeviceEnrollmentCompareRowInput
            {
                BioDeviceId = r.BioDeviceId,
                DeviceName = r.DeviceName,
                EmployeeId = NicIdNormalizer.Normalize(r.EmployeeId),
                DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? null : r.DisplayName.Trim(),
            })
            .Where(r => !string.IsNullOrEmpty(r.EmployeeId))
            .ToList();

        var byEmp = list.GroupBy(r => r.EmployeeId, StringComparer.Ordinal);
        var shared = new List<DeviceEnrollmentCompareGroup>();
        var uniqueByDevice = new Dictionary<long, List<string>>();
        var nameConflicts = 0;

        foreach (var g in byEmp)
        {
            var deviceGroups = g.GroupBy(x => x.BioDeviceId).ToList();
            if (deviceGroups.Count > 1)
            {
                var names = g.Select(x => x.DisplayName ?? "").Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var mismatch = names.Count > 1;
                if (mismatch) nameConflicts++;

                shared.Add(new DeviceEnrollmentCompareGroup
                {
                    EmployeeId = g.Key,
                    NameMismatch = mismatch,
                    Devices = deviceGroups
                        .Select(dg => new DeviceEnrollmentCompareHit
                        {
                            BioDeviceId = dg.Key,
                            DeviceName = dg.First().DeviceName,
                            DisplayName = dg.First().DisplayName,
                        })
                        .OrderBy(d => d.BioDeviceId)
                        .ToList(),
                });
            }
            else
            {
                var devId = deviceGroups[0].Key;
                if (!uniqueByDevice.TryGetValue(devId, out var bucket))
                {
                    bucket = [];
                    uniqueByDevice[devId] = bucket;
                }

                bucket.Add(g.Key);
            }
        }

        var uniqueSlices = uniqueByDevice
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var name = list.FirstOrDefault(r => r.BioDeviceId == kv.Key)?.DeviceName;
                return new DeviceEnrollmentUniqueSlice
                {
                    BioDeviceId = kv.Key,
                    DeviceName = name,
                    Count = kv.Value.Count,
                    EmployeeIds = kv.Value.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                };
            })
            .ToList();

        return new DeviceEnrollmentCompareResult
        {
            TotalRows = list.Count,
            DistinctEmployeeIds = byEmp.Count(),
            OnMultipleDevices = shared.Count,
            NameConflicts = nameConflicts,
            SharedAcrossDevices = shared.OrderBy(s => s.EmployeeId, StringComparer.Ordinal).ToList(),
            UniquePerDevice = uniqueSlices,
        };
    }
}