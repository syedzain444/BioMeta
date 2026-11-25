namespace BiometricDashboard.Core.Entities;

public sealed class AttendancePullResult
{
    public IReadOnlyList<AttendanceRecord> Records { get; init; } = [];
    public int? RecordTotalOnDevice { get; init; }
    public string? DeviceSerial { get; init; }
    public bool Succeeded { get; init; }
}
