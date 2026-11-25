namespace BiometricDashboard.Core.Entities;

public sealed class EmployeeRegistryEntry
{
    public long RegistryId { get; set; }
    public string EmployeeId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Department { get; set; }
    public string? CardNumber { get; set; }
    public string? Authority { get; set; }
    public long? DefaultDeviceId { get; set; }
    public string? BiometricStatus { get; set; }
    public bool HasFaceData { get; set; }
    public DateTime UpdatedAt { get; set; }
}