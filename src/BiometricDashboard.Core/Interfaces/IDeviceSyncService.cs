using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public interface IDeviceSyncService
{
    DeviceSnapshot? LatestSnapshot { get; }
    IReadOnlyDictionary<long, DeviceSnapshot> SnapshotsByDeviceId { get; }
    DeviceSnapshot? GetSnapshot(long deviceId);
    event Func<IReadOnlyList<AttendanceRecord>, Task>? NewRecordsReceived;

    /// <summary>One HDCP command (GetRecord, or GetDeviceInfo when requested). Skipped when SyncMode is Push.</summary>
    Task<DevicePullSyncResult> PullOnceAsync(long bioDeviceId, bool refreshDeviceInfo = false, CancellationToken cancellationToken = default);

    Task NotifyPushedRecordsAsync(IReadOnlyList<AttendanceRecord> records, CancellationToken cancellationToken = default);

    void MarkPushHeartbeat(long bioDeviceId, string? deviceSerial = null);

    /// <summary>GetDeviceInfo + GetRecord probe; updates DB last seen and in-memory snapshot.</summary>
    Task<DeviceConnectionTestResult> ProbeDeviceAsync(long bioDeviceId, CancellationToken cancellationToken = default);

    /// <summary>Probe by IP/port only (create form — does not write DB).</summary>
    Task<DeviceConnectionTestResult> ProbeEndpointAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>Records a failed manual probe (snapshot + DB) when the probe throws before completing.</summary>
    Task RecordProbeFailureAsync(long bioDeviceId, string error, CancellationToken cancellationToken = default);
}
