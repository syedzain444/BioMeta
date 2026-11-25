namespace BiometricDashboard.Core.Entities;

public sealed class BiometricDevice : AuditableEntity
{
    public long DeviceId { get; set; }
    public string DeviceIp { get; set; } = "";
    public int DevicePort { get; set; } = 9922;
    public string? AdminId { get; set; }
    public string? AdminPasswordProtected { get; set; }
    public string DeviceName { get; set; } = "";
    public string? DeviceLocation { get; set; }
    public string? DeviceSerial { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsPrimary { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastSyncError { get; set; }
}

public sealed class DeviceEndpoint
{
    public long DeviceId { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9922;
    public string RecordStart { get; set; } = "2020-01-01 00:00:00";
    public string RecordEnd { get; set; } = "2030-12-31 23:59:59";
}
