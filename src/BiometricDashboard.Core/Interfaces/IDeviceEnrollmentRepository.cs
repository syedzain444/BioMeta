using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public interface IDeviceEnrollmentRepository
{
    Task<Guid> SaveFetchBatchAsync(
        int deviceCount,
        int rowCount,
        long? actorUserId,
        string? note,
        CancellationToken cancellationToken = default);

    Task<int> UpsertFromDevicePullAsync(
        Guid batchId,
        long bioDeviceId,
        IReadOnlyList<DeviceEmployeeRecord> employees,
        long? actorUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceEnrollmentRecord>> ListByDevicesAsync(
        IReadOnlyList<long> bioDeviceIds,
        CancellationToken cancellationToken = default);

    DeviceEnrollmentCompareResult CompareRows(IEnumerable<DeviceEnrollmentCompareRowInput> rows);
}

public sealed class DeviceEnrollmentCompareRowInput
{
    public long BioDeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string EmployeeId { get; set; } = "";
    public string? DisplayName { get; set; }
}