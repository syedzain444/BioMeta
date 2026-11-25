namespace BiometricDashboard.Core.Entities;

/// <summary>Portal copy of one employee enrollment on a specific device.</summary>
public sealed class DeviceEnrollmentRecord
{
    public long EnrollmentId { get; set; }
    public long BioDeviceId { get; set; }
    public string EmployeeId { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Department { get; set; }
    public string? CardNumber { get; set; }
    public string? Authority { get; set; }
    public string? CheckType { get; set; }
    public int FaceTemplateCount { get; set; }
    public string? BiometricStatus { get; set; }
    public DateTime? EnrolledAt { get; set; }
    public string? EnrolledSource { get; set; }
    public DateTime LastFetchedAt { get; set; }
    public string? FetchBatchId { get; set; }
}

public sealed class DeviceEnrollmentCompareResult
{
    public int TotalRows { get; set; }
    public int DistinctEmployeeIds { get; set; }
    public int OnMultipleDevices { get; set; }
    public int NameConflicts { get; set; }
    public IReadOnlyList<DeviceEnrollmentCompareGroup> SharedAcrossDevices { get; set; } = [];
    public IReadOnlyList<DeviceEnrollmentUniqueSlice> UniquePerDevice { get; set; } = [];
}

public sealed class DeviceEnrollmentCompareGroup
{
    public string EmployeeId { get; set; } = "";
    public IReadOnlyList<DeviceEnrollmentCompareHit> Devices { get; set; } = [];
    public bool NameMismatch { get; set; }
}

public sealed class DeviceEnrollmentCompareHit
{
    public long BioDeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? DisplayName { get; set; }
}

public sealed class DeviceEnrollmentUniqueSlice
{
    public long BioDeviceId { get; set; }
    public string? DeviceName { get; set; }
    public int Count { get; set; }
    public IReadOnlyList<string> EmployeeIds { get; set; } = [];
}