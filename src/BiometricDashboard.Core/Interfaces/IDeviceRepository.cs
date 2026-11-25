using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public interface IDeviceRepository
{
    Task<bool> IsSetupCompleteAsync(CancellationToken cancellationToken = default);
    Task MarkSetupCompleteAsync(CancellationToken cancellationToken = default);
    Task<int> CountActiveDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BiometricDevice>> ListDevicesAsync(bool activeOnly = false, CancellationToken cancellationToken = default);
    Task<BiometricDevice?> GetByIdAsync(long deviceId, CancellationToken cancellationToken = default);
    Task<BiometricDevice?> GetPrimaryAsync(CancellationToken cancellationToken = default);
    Task<long> CreateAsync(BiometricDevice device, long? actorUserId, CancellationToken cancellationToken = default);
    Task UpdateAsync(BiometricDevice device, long? actorUserId, CancellationToken cancellationToken = default);
    Task SoftDeleteAsync(long deviceId, long actorUserId, CancellationToken cancellationToken = default);
    Task UpdateSyncStatusAsync(long deviceId, string? serial, bool online, string? error, CancellationToken cancellationToken = default);
}
