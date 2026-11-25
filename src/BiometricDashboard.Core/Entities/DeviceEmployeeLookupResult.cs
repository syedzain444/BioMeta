namespace BiometricDashboard.Core.Entities;

public sealed class DeviceEmployeeLookupResult
{
    public bool Found { get; set; }
    public string? Error { get; set; }
    public DeviceEmployeeRecord? Employee { get; set; }
    public bool HasDisplayPhoto { get; set; }
    /// <summary>photo_data, jpeg-embedded, face-template-only, etc.</summary>
    public string? PhotoHint { get; set; }
    public string? HdcpMethod { get; set; }
}