namespace BiometricDashboard.Core.Entities;

public sealed class DeviceEmployeeRecord
{
    public string EmployeeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CardNumber { get; set; }
    public string? Department { get; set; }
    public string? Authority { get; set; }
    public string? Password { get; set; }
    public int FaceTemplateCount { get; set; }
    /// <summary>face, finger, card, … from device fields when present.</summary>
    public string? CheckType { get; set; }
    /// <summary>Optional base64 face template for Hanwang upload commands.</summary>
    public string? FaceDataBase64 { get; set; }
    /// <summary>Device 大头照 / photo_data when present in GetEmployee.</summary>
    public string? PhotoDataBase64 { get; set; }
    public bool FoundOnDevice { get; set; }
    public long? BioDeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? DeviceIp { get; set; }
    /// <summary>From device fields when available, else earliest portal punch on this device.</summary>
    public DateTime? EnrolledAtUtc { get; set; }
    public string? EnrolledAtSource { get; set; }
}

public sealed class DeviceEmployeePullResult
{
    public bool Succeeded { get; set; }
    /// <summary>True when Found matches DeviceEnrolledCount from GetDeviceInfo (or pull was inconclusive but Found &gt; 0).</summary>
    public bool IsComplete { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
    public string? Method { get; set; }
    public int Requested { get; set; }
    public int Found { get; set; }
    public int DeviceEnrolledCount { get; set; }
    public long ElapsedMs { get; set; }
    public IReadOnlyList<DeviceEmployeeRecord> Employees { get; set; } = [];
}
