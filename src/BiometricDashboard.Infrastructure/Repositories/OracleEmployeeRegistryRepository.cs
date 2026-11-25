using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Utilities;
using BiometricDashboard.Infrastructure.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace BiometricDashboard.Infrastructure.Repositories;

public sealed class OracleEmployeeRegistryRepository(
    OracleConnectionFactory factory,
    ILogger<OracleEmployeeRegistryRepository> logger) : IEmployeeRegistryRepository
{
    public async Task UpsertManyAsync(
        IReadOnlyList<EmployeeImportRow> rows,
        long? defaultDeviceId,
        long? actorUserId,
        string? sourceTag,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0) return;
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);

        foreach (var row in rows.Where(r => r.IsValid))
        {
            var id = NicIdNormalizer.Normalize(row.EmployeeId);
            if (string.IsNullOrEmpty(id)) continue;
            try
            {
                await conn.ExecuteAsync(
                    """
                    MERGE INTO BIO_EMPLOYEE_REGISTRY t
                    USING (SELECT :EmployeeId EMPLOYEE_ID FROM DUAL) s
                    ON (t.EMPLOYEE_ID = s.EMPLOYEE_ID AND t.DELETED_AT IS NULL)
                    WHEN MATCHED THEN UPDATE SET
                      DISPLAY_NAME = :DisplayName,
                      DEPARTMENT = :Department,
                      CARD_NUMBER = :CardNumber,
                      AUTHORITY = :Authority,
                      DEFAULT_DEVICE_ID = NVL(:DefaultDeviceId, t.DEFAULT_DEVICE_ID),
                      HAS_FACE_DATA = :HasFace,
                      SOURCE_TAG = :SourceTag,
                      UPDATED_BY = :Actor,
                      UPDATED_AT = SYSTIMESTAMP
                    WHEN NOT MATCHED THEN INSERT
                      (EMPLOYEE_ID, DISPLAY_NAME, DEPARTMENT, CARD_NUMBER, AUTHORITY, DEFAULT_DEVICE_ID, HAS_FACE_DATA, SOURCE_TAG, CREATED_BY, UPDATED_BY)
                    VALUES
                      (:EmployeeId, :DisplayName, :Department, :CardNumber, :Authority, :DefaultDeviceId, :HasFace, :SourceTag, :Actor, :Actor)
                    """,
                    new
                    {
                        EmployeeId = id,
                        DisplayName = row.Name,
                        Department = row.Department,
                        CardNumber = row.CardNumber,
                        Authority = row.Authority,
                        DefaultDeviceId = defaultDeviceId,
                        HasFace = string.IsNullOrWhiteSpace(row.FaceDataBase64) ? "N" : "Y",
                        SourceTag = sourceTag,
                        Actor = actorUserId,
                    });
            }
            catch (OracleException ex)
            {
                logger.LogWarning(ex, "Registry upsert failed for {EmployeeId}", id);
                throw;
            }
        }
    }

    public async Task<IReadOnlyList<EmployeeRegistryEntry>> ListActiveAsync(long? deviceId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var sql = """
            SELECT REGISTRY_ID RegistryId, EMPLOYEE_ID EmployeeId, DISPLAY_NAME DisplayName, DEPARTMENT Department,
                   CARD_NUMBER CardNumber, AUTHORITY Authority, DEFAULT_DEVICE_ID DefaultDeviceId,
                   BIOMETRIC_STATUS BiometricStatus,
                   CASE WHEN HAS_FACE_DATA = 'Y' THEN 1 ELSE 0 END HasFaceData, UPDATED_AT UpdatedAt
            FROM BIO_EMPLOYEE_REGISTRY WHERE DELETED_AT IS NULL
            """;
        if (deviceId is > 0)
            sql += " AND (DEFAULT_DEVICE_ID IS NULL OR DEFAULT_DEVICE_ID = :DeviceId)";
        sql += " ORDER BY EMPLOYEE_ID";
        var rows = await conn.QueryAsync<EmployeeRegistryEntry>(
            new CommandDefinition(sql, new { DeviceId = deviceId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<string>> ListEmployeeIdsAsync(long? deviceId, CancellationToken cancellationToken = default)
    {
        var list = await ListActiveAsync(deviceId, cancellationToken);
        return list.Select(e => e.EmployeeId).ToList();
    }
}