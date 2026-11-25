namespace BiometricDashboard.Core.Entities;

public sealed class DevicePullSyncResult
{
    public IReadOnlyList<AttendanceRecord> Added { get; init; } = [];
    public DeviceSnapshot? Snapshot { get; init; }
}
