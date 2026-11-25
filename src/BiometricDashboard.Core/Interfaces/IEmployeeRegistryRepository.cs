using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public interface IEmployeeRegistryRepository
{
    Task UpsertManyAsync(IReadOnlyList<EmployeeImportRow> rows, long? defaultDeviceId, long? actorUserId, string? sourceTag, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EmployeeRegistryEntry>> ListActiveAsync(long? deviceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListEmployeeIdsAsync(long? deviceId, CancellationToken cancellationToken = default);
}