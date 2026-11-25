namespace BiometricDashboard.Core.Entities;

public sealed class DeviceConnectionTestResult
{
    public string? Host { get; init; }
    public int Port { get; init; }
    public bool TcpReachable { get; init; }
    public long TcpLatencyMs { get; init; }
    public string? TcpError { get; init; }
    public bool Online { get; init; }
    public bool DataChannelOk { get; init; }
    /// <summary>HDCP GetDeviceInfo succeeded (device application layer).</summary>
    public bool Ok => Online;
    public int SampleRecordCount { get; init; }
    public int? TotalRecordsOnDevice { get; init; }
    /// <summary>Attendance rows stored in portal for this device (push/pull).</summary>
    public int? PortalAttendanceRows { get; init; }
    public string? ProbeWindowStart { get; init; }
    public string? ProbeWindowEnd { get; init; }
    public DeviceSnapshot? Snapshot { get; init; }
    public string? Message { get; init; }
}
