namespace BiometricDashboard.Core.Entities;

public sealed class DeviceEmployeeUploadProgress
{
    public int Index { get; init; }
    public int Total { get; init; }
    public string EmployeeId { get; init; } = "";
    public int UploadedSoFar { get; init; }
    public string Phase { get; init; } = "";
}