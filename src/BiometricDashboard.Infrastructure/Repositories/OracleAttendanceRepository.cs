using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Utilities;
using BiometricDashboard.Infrastructure.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace BiometricDashboard.Infrastructure.Repositories;

public sealed class OracleAttendanceRepository : IAttendanceRepository, IAttendanceLogQuery
{
    private readonly OracleConnectionFactory _factory;
    private readonly IDeviceRepository _devices;
    private readonly ILogger<OracleAttendanceRepository> _logger;

    private static bool? _hasDeletedAt;
    private static bool? _hasBioDeviceId;
    private static bool? _hasUpdatedAt;

    private const string InsertSql = """
        INSERT INTO BIOMETRIC_ATTENDANCE_LOG
          (EMPLOYEE_ID, EMPLOYEE_NAME, PUNCH_TIME, WORK_CODE, STATUS, AUTHORITY, CARD_SOURCE, DEVICE_SERIAL, SYNCED_AT, DEDUPE_KEY, CREATED_BY, BIO_DEVICE_ID)
        VALUES
          (:EmployeeId, :EmployeeName, :PunchTime, :WorkCode, :Status, :Authority, :CardSource, :DeviceSerial, :SyncedAt, :DedupeKey, :CreatedBy, :BioDeviceId)
        """;

    private const string SelectCore = """
        EMPLOYEE_ID EmployeeId, EMPLOYEE_NAME EmployeeName, PUNCH_TIME PunchTime,
        WORK_CODE WorkCode, STATUS Status, AUTHORITY Authority, CARD_SOURCE CardSource,
        DEVICE_SERIAL DeviceSerial, SYNCED_AT SyncedAt, DEDUPE_KEY DedupeKey
        """;

    public OracleAttendanceRepository(
        OracleConnectionFactory factory,
        IDeviceRepository devices,
        ILogger<OracleAttendanceRepository> logger)
    {
        _factory = factory;
        _devices = devices;
        _logger = logger;
    }

    private static async Task EnsureSchemaAsync(OracleConnection conn, CancellationToken cancellationToken)
    {
        if (_hasDeletedAt.HasValue) return;

        const string sql = """
            SELECT column_name FROM user_tab_columns
            WHERE table_name = 'BIOMETRIC_ATTENDANCE_LOG'
              AND column_name IN ('DELETED_AT','BIO_DEVICE_ID','UPDATED_AT')
            """;
        var cols = (await conn.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: cancellationToken)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _hasDeletedAt = cols.Contains("DELETED_AT");
        _hasBioDeviceId = cols.Contains("BIO_DEVICE_ID");
        _hasUpdatedAt = cols.Contains("UPDATED_AT");
    }

    private static string SelectList() =>
        _hasBioDeviceId == true ? SelectCore + ", BIO_DEVICE_ID BioDeviceId" : SelectCore + ", 0 BioDeviceId";

    private static string WhereBase() =>
        _hasDeletedAt == true ? "DELETED_AT IS NULL" : "1=1";

    private async Task<string> BuildDeviceScopeAsync(
        OracleConnection conn,
        long? bioDeviceId,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        if (bioDeviceId is not > 0 || _hasBioDeviceId != true)
            return "";

        parameters.Add("DeviceId", bioDeviceId.Value);

        string? serial = null;
        try
        {
            serial = (await conn.ExecuteScalarAsync<string>(
                "SELECT DEVICE_SERIAL FROM BIO_DEVICE WHERE DEVICE_ID = :Id AND ROWNUM = 1",
                new { Id = bioDeviceId.Value }))?.Trim();
        }
        catch (OracleException)
        {
            return " AND BIO_DEVICE_ID = :DeviceId";
        }

        if (string.IsNullOrEmpty(serial))
            return " AND BIO_DEVICE_ID = :DeviceId";

        parameters.Add("DeviceSerial", serial);
        return " AND (BIO_DEVICE_ID = :DeviceId OR (BIO_DEVICE_ID IS NULL AND DEVICE_SERIAL = :DeviceSerial))";
    }

    private async Task EnrichDeviceFieldsAsync(IList<AttendanceRecord> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        try
        {
            var deviceList = await _devices.ListDevicesAsync(cancellationToken: cancellationToken);
            var map = deviceList.ToDictionary(d => d.DeviceId);
            foreach (var r in rows)
            {
                if (r.BioDeviceId is > 0 && map.TryGetValue(r.BioDeviceId.Value, out var dev))
                {
                    r.DeviceName = dev.DeviceName;
                    r.DeviceLocation = dev.DeviceLocation ?? "";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Device enrich skipped");
        }
    }

    public async Task<int> CountAsync(long? bioDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(conn, cancellationToken);

        var p = new DynamicParameters();
        var scope = await BuildDeviceScopeAsync(conn, bioDeviceId, p, cancellationToken);
        return await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM BIOMETRIC_ATTENDANCE_LOG WHERE {WhereBase()}{scope}",
            p);
    }

    public async Task<IReadOnlyList<string>> GetDistinctEmployeeIdsAsync(
        long? bioDeviceId = null,
        int maxRows = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(conn, cancellationToken);

        var p = new DynamicParameters();
        p.Add("Max", maxRows);
        var scope = await BuildDeviceScopeAsync(conn, bioDeviceId, p, cancellationToken);
        var sql = $"""
            SELECT EmployeeId FROM (
              SELECT DISTINCT EMPLOYEE_ID EmployeeId
              FROM BIOMETRIC_ATTENDANCE_LOG
              WHERE {WhereBase()}{scope}
              ORDER BY EMPLOYEE_ID
            )
            WHERE ROWNUM <= :Max
            """;
        var rows = await conn.QueryAsync<string>(sql, p);
        return rows.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    }

    public async Task<IReadOnlyList<string>> GetEmployeeIdsWithFirstPunchBetweenAsync(
        DateTime startInclusive,
        DateTime endExclusive,
        long? bioDeviceId = null,
        int maxRows = 10000,
        CancellationToken cancellationToken = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(conn, cancellationToken);

        var p = new DynamicParameters();
        p.Add("Start", startInclusive);
        p.Add("End", endExclusive);
        p.Add("Max", Math.Clamp(maxRows, 1, 50000));
        var scope = await BuildDeviceScopeAsync(conn, bioDeviceId, p, cancellationToken);
        var sql = $"""
            SELECT EmployeeId FROM (
              SELECT TRIM(EMPLOYEE_ID) EmployeeId
              FROM BIOMETRIC_ATTENDANCE_LOG
              WHERE {WhereBase()}{scope}
              GROUP BY TRIM(EMPLOYEE_ID)
              HAVING MIN(PUNCH_TIME) >= :Start AND MIN(PUNCH_TIME) < :End
            )
            WHERE ROWNUM <= :Max
            """;
        var rows = await conn.QueryAsync<string>(sql, p);
        return rows
            .Select(NicIdNormalizer.Normalize)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<AttendanceRecord>> GetRecentAsync(int count, long? bioDeviceId = null, CancellationToken cancellationToken = default)
    {
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(conn, cancellationToken);

        var p = new DynamicParameters();
        p.Add("Take", count);
        var scope = await BuildDeviceScopeAsync(conn, bioDeviceId, p, cancellationToken);

        var sql = $"""
            SELECT EmployeeId, EmployeeName, PunchTime, WorkCode, Status, Authority, CardSource, DeviceSerial, SyncedAt, DedupeKey, BioDeviceId
            FROM (
              SELECT {SelectList()}
              FROM BIOMETRIC_ATTENDANCE_LOG
              WHERE {WhereBase()}{scope}
              ORDER BY PUNCH_TIME DESC
            )
            WHERE ROWNUM <= :Take
            """;

        var rows = await conn.QueryAsync<AttendanceLogRow>(sql, p);
        var list = rows.Select(ToDomain).ToList();
        await EnrichDeviceFieldsAsync(list, cancellationToken);
        return list;
    }

    public async Task<IReadOnlyList<AttendanceRecord>> UpsertManyAsync(
        IEnumerable<AttendanceRecord> records,
        CancellationToken cancellationToken = default)
    {
        var added = new List<AttendanceRecord>();
        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(conn, cancellationToken);

        foreach (var record in records)
        {
            var legacyKey = $"{record.EmployeeId}|{record.PunchTime:yyyy-MM-dd HH:mm:ss}";
            var exists = await conn.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM BIOMETRIC_ATTENDANCE_LOG
                WHERE DEDUPE_KEY = :Key OR DEDUPE_KEY = :LegacyKey
                """,
                new { Key = record.DedupeKey, LegacyKey = legacyKey });
            if (exists > 0)
            {
                if (record.BioDeviceId is > 0 && _hasBioDeviceId == true)
                {
                    var updateSql = _hasUpdatedAt == true
                        ? """
                          UPDATE BIOMETRIC_ATTENDANCE_LOG
                          SET BIO_DEVICE_ID = :BioDeviceId,
                              DEVICE_SERIAL = NVL(DEVICE_SERIAL, :DeviceSerial),
                              UPDATED_AT = SYSTIMESTAMP
                          WHERE (DEDUPE_KEY = :Key OR DEDUPE_KEY = :LegacyKey)
                            AND BIO_DEVICE_ID IS NULL
                          """
                        : """
                          UPDATE BIOMETRIC_ATTENDANCE_LOG
                          SET BIO_DEVICE_ID = :BioDeviceId,
                              DEVICE_SERIAL = NVL(DEVICE_SERIAL, :DeviceSerial)
                          WHERE (DEDUPE_KEY = :Key OR DEDUPE_KEY = :LegacyKey)
                            AND BIO_DEVICE_ID IS NULL
                          """;
                    await conn.ExecuteAsync(updateSql, new
                    {
                        record.BioDeviceId,
                        record.DeviceSerial,
                        Key = record.DedupeKey,
                        LegacyKey = legacyKey,
                    });
                }
                continue;
            }

            try
            {
                if (_hasBioDeviceId == true)
                {
                    await conn.ExecuteAsync(InsertSql, new
                    {
                        record.EmployeeId,
                        record.EmployeeName,
                        PunchTime = record.PunchTime,
                        record.WorkCode,
                        record.Status,
                        record.Authority,
                        record.CardSource,
                        record.DeviceSerial,
                        SyncedAt = record.SyncedAt,
                        DedupeKey = record.DedupeKey,
                        CreatedBy = 1L,
                        BioDeviceId = record.BioDeviceId,
                    });
                }
                else
                {
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO BIOMETRIC_ATTENDANCE_LOG
                          (EMPLOYEE_ID, EMPLOYEE_NAME, PUNCH_TIME, WORK_CODE, STATUS, AUTHORITY, CARD_SOURCE, DEVICE_SERIAL, SYNCED_AT, DEDUPE_KEY)
                        VALUES
                          (:EmployeeId, :EmployeeName, :PunchTime, :WorkCode, :Status, :Authority, :CardSource, :DeviceSerial, :SyncedAt, :DedupeKey)
                        """,
                        new
                        {
                            record.EmployeeId,
                            record.EmployeeName,
                            PunchTime = record.PunchTime,
                            record.WorkCode,
                            record.Status,
                            record.Authority,
                            record.CardSource,
                            record.DeviceSerial,
                            SyncedAt = record.SyncedAt,
                            DedupeKey = record.DedupeKey,
                        });
                }
                added.Add(record);
            }
            catch (OracleException ex) when (ex.Number == 1)
            {
                _logger.LogDebug("Duplicate dedupe key skipped");
            }
            catch (OracleException ex)
            {
                _logger.LogWarning(ex, "Insert attendance failed for {Employee} @ {Time}", record.EmployeeId, record.PunchTime);
            }
        }

        return added;
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> GetFirstPunchByEmployeeIdsAsync(
        IEnumerable<string> employeeIds,
        long? bioDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        var ids = employeeIds
            .Select(NicIdNormalizer.Normalize)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
            return new Dictionary<string, DateTime>(StringComparer.Ordinal);

        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(conn, cancellationToken);

        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(400))
        {
            var p = new DynamicParameters();
            var placeholders = string.Join(", ", chunk.Select((_, i) => $":Id{i}"));
            for (var i = 0; i < chunk.Length; i++)
                p.Add($"Id{i}", chunk[i]);

            var scope = await BuildDeviceScopeAsync(conn, bioDeviceId, p, cancellationToken);
            var sql = $"""
                SELECT TRIM(EMPLOYEE_ID) EmployeeId, MIN(PUNCH_TIME) FirstPunch
                FROM BIOMETRIC_ATTENDANCE_LOG
                WHERE {WhereBase()}{scope}
                  AND (
                        TRIM(EMPLOYEE_ID) IN ({placeholders})
                     OR LTRIM(TRIM(EMPLOYEE_ID), '0') IN ({placeholders})
                  )
                GROUP BY TRIM(EMPLOYEE_ID)
                """;

            var rows = await conn.QueryAsync<(string EmployeeId, DateTime FirstPunch)>(sql, p);
            foreach (var row in rows)
            {
                var key = NicIdNormalizer.Normalize(row.EmployeeId);
                if (string.IsNullOrEmpty(key)) continue;
                if (!result.TryGetValue(key, out var cur) || row.FirstPunch < cur)
                    result[key] = row.FirstPunch;
            }
        }

        return result;
    }

    public async Task<AttendanceLogPage> QueryAsync(AttendanceLogQuery query, CancellationToken cancellationToken = default)
    {
        var whereParts = new List<string> { WhereBase() };
        var p = new DynamicParameters();

        if (query.From.HasValue)
        {
            whereParts.Add("PUNCH_TIME >= :FromDate");
            p.Add("FromDate", query.From.Value);
        }
        if (query.To.HasValue)
        {
            whereParts.Add("PUNCH_TIME <= :ToDate");
            p.Add("ToDate", query.To.Value);
        }
        if (query.FromTimeOfDay.HasValue)
        {
            whereParts.Add("TO_CHAR(PUNCH_TIME, 'HH24:MI:SS') >= :FromTime");
            p.Add("FromTime", query.FromTimeOfDay.Value.ToString(@"hh\:mm\:ss"));
        }
        if (query.ToTimeOfDay.HasValue)
        {
            whereParts.Add("TO_CHAR(PUNCH_TIME, 'HH24:MI:SS') <= :ToTime");
            p.Add("ToTime", query.ToTimeOfDay.Value.ToString(@"hh\:mm\:ss"));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            whereParts.Add("(LOWER(EMPLOYEE_ID) LIKE :S OR LOWER(EMPLOYEE_NAME) LIKE :S)");
            p.Add("S", $"%{query.Search.Trim().ToLower()}%");
        }

        await using var conn = _factory.Create();
        await conn.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(conn, cancellationToken);

        var deviceScope = await BuildDeviceScopeAsync(conn, query.BioDeviceId, p, cancellationToken);
        var whereSql = string.Join(" AND ", whereParts) + deviceScope;

        var total = await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM BIOMETRIC_ATTENDANCE_LOG WHERE {whereSql}", p);

        p.Add("Skip", query.Skip);
        p.Add("Take", query.Take);

        var sql = $"""
            SELECT EmployeeId, EmployeeName, PunchTime, WorkCode, Status, Authority, CardSource, DeviceSerial, SyncedAt, DedupeKey, BioDeviceId
            FROM (
              SELECT inner_q.*, ROWNUM rnum FROM (
                SELECT {SelectList()}
                FROM BIOMETRIC_ATTENDANCE_LOG
                WHERE {whereSql}
                ORDER BY PUNCH_TIME DESC
              ) inner_q WHERE ROWNUM <= :Skip + :Take
            ) WHERE rnum > :Skip
            """;

        var rows = await conn.QueryAsync<AttendanceLogRow>(sql, p);
        var list = rows.Select(ToDomain).ToList();
        await EnrichDeviceFieldsAsync(list, cancellationToken);
        return new AttendanceLogPage(total, list);
    }

    private static AttendanceRecord ToDomain(AttendanceLogRow r) => new()
    {
        EmployeeId = r.EmployeeId,
        EmployeeName = r.EmployeeName,
        PunchTime = r.PunchTime,
        WorkCode = r.WorkCode ?? "",
        Status = r.Status ?? "",
        Authority = r.Authority ?? "",
        CardSource = r.CardSource ?? "",
        DeviceSerial = r.DeviceSerial ?? "",
        SyncedAt = r.SyncedAt,
        BioDeviceId = r.BioDeviceId == 0 ? null : r.BioDeviceId,
    };

    private sealed class AttendanceLogRow
    {
        public string EmployeeId { get; set; } = "";
        public string EmployeeName { get; set; } = "";
        public DateTime PunchTime { get; set; }
        public string? WorkCode { get; set; }
        public string? Status { get; set; }
        public string? Authority { get; set; }
        public string? CardSource { get; set; }
        public string? DeviceSerial { get; set; }
        public DateTime SyncedAt { get; set; }
        public string DedupeKey { get; set; } = "";
        public long BioDeviceId { get; set; }
    }
}
