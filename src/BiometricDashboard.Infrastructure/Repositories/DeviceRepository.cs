using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Infrastructure.Persistence;
using Dapper;

namespace BiometricDashboard.Infrastructure.Repositories;

public sealed class DeviceRepository(OracleConnectionFactory factory) : IDeviceRepository
{
    private const string DeviceSelect = """
        SELECT DEVICE_ID DeviceId, DEVICE_IP DeviceIp, DEVICE_PORT DevicePort, ADMIN_ID AdminId,
               ADMIN_PASSWORD_PROTECT AdminPasswordProtected, DEVICE_NAME DeviceName,
               DEVICE_LOCATION DeviceLocation, DEVICE_SERIAL DeviceSerial,
               CASE WHEN IS_ACTIVE = 'Y' THEN 1 ELSE 0 END IsActive,
               CASE WHEN IS_PRIMARY = 'Y' THEN 1 ELSE 0 END IsPrimary,
               LAST_SEEN_AT LastSeenAt, LAST_SYNC_ERROR LastSyncError,
               CREATED_BY CreatedBy, UPDATED_BY UpdatedBy, DELETED_BY DeletedBy,
               CREATED_AT CreatedAt, UPDATED_AT UpdatedAt, DELETED_AT DeletedAt
        FROM BIO_DEVICE WHERE DELETED_AT IS NULL
        """;

    public async Task<bool> IsSetupCompleteAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var v = await conn.ExecuteScalarAsync<string>(
            "SELECT SETTING_VALUE FROM BIO_PORTAL_SETTING WHERE SETTING_KEY = 'SETUP_COMPLETE'");
        return string.Equals(v, "Y", StringComparison.OrdinalIgnoreCase);
    }

    public async Task MarkSetupCompleteAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(
            """
            MERGE INTO BIO_PORTAL_SETTING t
            USING (SELECT 'SETUP_COMPLETE' AS k, 'Y' AS v FROM DUAL) s
            ON (t.SETTING_KEY = s.k)
            WHEN MATCHED THEN UPDATE SET SETTING_VALUE = s.v, UPDATED_AT = SYSTIMESTAMP
            WHEN NOT MATCHED THEN INSERT (SETTING_KEY, SETTING_VALUE) VALUES (s.k, s.v)
            """);
    }

    public async Task<int> CountActiveDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM BIO_DEVICE WHERE DELETED_AT IS NULL AND IS_ACTIVE = 'Y'");
    }

    public async Task<IReadOnlyList<BiometricDevice>> ListDevicesAsync(bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var sql = DeviceSelect + (activeOnly ? " AND IS_ACTIVE = 'Y'" : "") + " ORDER BY IS_PRIMARY DESC, DEVICE_NAME";
        var rows = await conn.QueryAsync<DeviceRow>(sql);
        return rows.Select(Map).ToList();
    }

    public async Task<BiometricDevice?> GetByIdAsync(long deviceId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var row = await conn.QueryFirstOrDefaultAsync<DeviceRow>(
            DeviceSelect + " AND DEVICE_ID = :Id", new { Id = deviceId });
        return row is null ? null : Map(row);
    }

    public async Task<BiometricDevice?> GetPrimaryAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var row = await conn.QueryFirstOrDefaultAsync<DeviceRow>(
            DeviceSelect + " AND IS_PRIMARY = 'Y' ORDER BY DEVICE_ID");
        if (row is not null) return Map(row);
        row = await conn.QueryFirstOrDefaultAsync<DeviceRow>(
            DeviceSelect + " ORDER BY DEVICE_ID");
        return row is null ? null : Map(row);
    }

    public async Task<long> CreateAsync(BiometricDevice device, long? actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        if (device.IsPrimary)
        {
            await conn.ExecuteAsync(
                "UPDATE BIO_DEVICE SET IS_PRIMARY = 'N', UPDATED_AT = SYSTIMESTAMP WHERE DELETED_AT IS NULL AND IS_PRIMARY = 'Y'");
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO BIO_DEVICE (DEVICE_IP, DEVICE_PORT, ADMIN_ID, ADMIN_PASSWORD_PROTECT, DEVICE_NAME, DEVICE_LOCATION,
              DEVICE_SERIAL, IS_ACTIVE, IS_PRIMARY, CREATED_BY)
            VALUES (:DeviceIp, :DevicePort, :AdminId, :AdminPasswordProtected, :DeviceName, :DeviceLocation,
              :DeviceSerial, :IsActive, :IsPrimary, :CreatedBy)
            """,
            new
            {
                device.DeviceIp,
                device.DevicePort,
                device.AdminId,
                device.AdminPasswordProtected,
                device.DeviceName,
                device.DeviceLocation,
                device.DeviceSerial,
                IsActive = device.IsActive ? "Y" : "N",
                IsPrimary = device.IsPrimary ? "Y" : "N",
                CreatedBy = actorUserId,
            });

        return await conn.ExecuteScalarAsync<long>(
            """
            SELECT DEVICE_ID FROM BIO_DEVICE
            WHERE DEVICE_IP = :DeviceIp AND DEVICE_PORT = :DevicePort AND DELETED_AT IS NULL
            ORDER BY DEVICE_ID DESC
            """,
            new { device.DeviceIp, device.DevicePort });
    }

    public async Task UpdateAsync(BiometricDevice device, long? actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        if (device.IsPrimary)
        {
            await conn.ExecuteAsync(
                "UPDATE BIO_DEVICE SET IS_PRIMARY = 'N', UPDATED_AT = SYSTIMESTAMP WHERE DELETED_AT IS NULL AND IS_PRIMARY = 'Y' AND DEVICE_ID <> :Id",
                new { Id = device.DeviceId });
        }

        await conn.ExecuteAsync(
            """
            UPDATE BIO_DEVICE SET DEVICE_IP = :DeviceIp, DEVICE_PORT = :DevicePort, ADMIN_ID = :AdminId,
              ADMIN_PASSWORD_PROTECT = :AdminPasswordProtected, DEVICE_NAME = :DeviceName, DEVICE_LOCATION = :DeviceLocation,
              DEVICE_SERIAL = :DeviceSerial, IS_ACTIVE = :IsActive, IS_PRIMARY = :IsPrimary,
              UPDATED_BY = :UpdatedBy, UPDATED_AT = SYSTIMESTAMP
            WHERE DEVICE_ID = :DeviceId AND DELETED_AT IS NULL
            """,
            new
            {
                device.DeviceIp,
                device.DevicePort,
                device.AdminId,
                device.AdminPasswordProtected,
                device.DeviceName,
                device.DeviceLocation,
                device.DeviceSerial,
                IsActive = device.IsActive ? "Y" : "N",
                IsPrimary = device.IsPrimary ? "Y" : "N",
                UpdatedBy = actorUserId,
                device.DeviceId,
            });
    }

    public async Task SoftDeleteAsync(long deviceId, long actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(
            """
            UPDATE BIO_DEVICE SET DELETED_AT = SYSTIMESTAMP, DELETED_BY = :Actor, IS_ACTIVE = 'N', UPDATED_AT = SYSTIMESTAMP
            WHERE DEVICE_ID = :Id
            """,
            new { Actor = actorUserId, Id = deviceId });
    }

    public async Task UpdateSyncStatusAsync(long deviceId, string? serial, bool online, string? error, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(
            """
            UPDATE BIO_DEVICE SET DEVICE_SERIAL = NVL(:Serial, DEVICE_SERIAL),
              LAST_SEEN_AT = CASE WHEN :Online = 1 THEN SYSTIMESTAMP ELSE NULL END,
              LAST_SYNC_ERROR = :Error, UPDATED_AT = SYSTIMESTAMP
            WHERE DEVICE_ID = :Id AND DELETED_AT IS NULL
            """,
            new { Serial = serial, Online = online ? 1 : 0, Error = error, Id = deviceId });
    }

    private static BiometricDevice Map(DeviceRow r) => new()
    {
        DeviceId = r.DeviceId,
        DeviceIp = r.DeviceIp?.Trim() ?? "",
        DevicePort = r.DevicePort,
        AdminId = r.AdminId,
        AdminPasswordProtected = r.AdminPasswordProtected,
        DeviceName = r.DeviceName ?? "",
        DeviceLocation = r.DeviceLocation,
        DeviceSerial = r.DeviceSerial,
        IsActive = r.IsActive != 0,
        IsPrimary = r.IsPrimary != 0,
        LastSeenAt = r.LastSeenAt,
        LastSyncError = r.LastSyncError,
        CreatedBy = r.CreatedBy,
        UpdatedBy = r.UpdatedBy,
        DeletedBy = r.DeletedBy,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
        DeletedAt = r.DeletedAt,
    };

    private sealed class DeviceRow
    {
        public long DeviceId { get; set; }
        public string? DeviceIp { get; set; }
        public int DevicePort { get; set; }
        public string? AdminId { get; set; }
        public string? AdminPasswordProtected { get; set; }
        public string? DeviceName { get; set; }
        public string? DeviceLocation { get; set; }
        public string? DeviceSerial { get; set; }
        public int IsActive { get; set; }
        public int IsPrimary { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public string? LastSyncError { get; set; }
        public long? CreatedBy { get; set; }
        public long? UpdatedBy { get; set; }
        public long? DeletedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }
}
