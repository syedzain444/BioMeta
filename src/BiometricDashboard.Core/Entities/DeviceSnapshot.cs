namespace BiometricDashboard.Core.Entities;

public class DeviceSnapshot
{
    public bool IsOnline { get; set; }
    public string? SerialNumber { get; set; }
    public string? Firmware { get; set; }
    public string? DeviceType { get; set; }
    public int EmployeeCount { get; set; }
    public int FaceEnrolled { get; set; }
    public int RecordCountOnDevice { get; set; }
    public string? LastError { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}
