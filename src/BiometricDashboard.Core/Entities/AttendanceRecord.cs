namespace BiometricDashboard.Core.Entities;

public class AttendanceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long? BioDeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public string DeviceLocation { get; set; } = "";
    public string DeviceSerial { get; set; } = "";
    public string EmployeeId { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public DateTime PunchTime { get; set; }
    public string WorkCode { get; set; } = "";
    public string Status { get; set; } = "";
    public string Authority { get; set; } = "";
    public string CardSource { get; set; } = "";
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;

    public string DedupeKey => $"{DeviceSerial}|{EmployeeId}|{PunchTime:yyyy-MM-dd HH:mm:ss}";
}
