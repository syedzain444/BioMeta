namespace BiometricDashboard.Core.Entities;

public enum EmployeeSyncJobState
{
    Queued,
    TriggeringDevice,
    Uploading,
    Verifying,
    Deleting,
    Completed,
    Failed,
    Cancelled,
}

public enum BiometricEnrollmentStatus
{
    Unknown,
    NoTemplate,
    FaceEnrolled,
    FingerprintEnrolled,
    CardOnly,
    Mixed,
}

public sealed class EmployeeImportRow
{
    public int Line { get; set; }
    public string EmployeeId { get; set; } = "";
    public string? Name { get; set; }
    public string? Department { get; set; }
    public string? CardNumber { get; set; }
    public string? Authority { get; set; }
    public string? FaceDataBase64 { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool IsValid => Errors.Count == 0 && !string.IsNullOrWhiteSpace(EmployeeId);
}

public sealed class EmployeeImportValidationResult
{
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public IReadOnlyList<EmployeeImportRow> Rows { get; set; } = [];
}

public sealed class EmployeeDiffItem
{
    public string EmployeeId { get; set; } = "";
    public string? Name { get; set; }
    public BiometricEnrollmentStatus BiometricStatus { get; set; }
    public int FaceTemplateCount { get; set; }
    public string? CheckType { get; set; }
    public string? Department { get; set; }
}

public sealed class EmployeeDiffResult
{
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public long DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public int PortalCount { get; set; }
    public int DeviceCount { get; set; }
    public int MatchedCount { get; set; }
    public List<EmployeeDiffItem> OnlyOnPortal { get; set; } = [];
    public List<EmployeeDiffItem> OnlyOnDevice { get; set; } = [];
    public List<EmployeeDiffItem> OnBoth { get; set; } = [];
    public long ElapsedMs { get; set; }
}

public sealed class DeviceSyncCompareSnapshot
{
    public int SourceCount { get; set; }
    public int TargetCount { get; set; }
    public int MissingOnTarget { get; set; }
    public int AlreadyOnBoth { get; set; }
}

public sealed class EmployeeSyncJob
{
    public Guid JobId { get; init; }
    public string JobKind { get; set; } = "upload";
    public long DeviceId { get; init; }
    public long SourceDeviceId { get; set; }
    public string DeviceIp { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string? SourceDeviceName { get; set; }
    public int SkippedAlreadyOnTarget { get; set; }
    public DeviceSyncCompareSnapshot? CompareBefore { get; set; }
    public DeviceSyncCompareSnapshot? CompareAfter { get; set; }
    public EmployeeSyncJobState State { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public int Total { get; set; }
    public int Uploaded { get; set; }
    public int Failed { get; set; }
    public int Verified { get; set; }
    public int Deleted { get; set; }
    public string? LastError { get; set; }
    public string? LogLine { get; set; }
    public IReadOnlyList<EmployeeSyncLineResult> LineResults { get; set; } = [];
}

public sealed class EmployeeSyncLineResult
{
    public string EmployeeId { get; set; } = "";
    public string? Name { get; set; }
    public bool Succeeded { get; set; }
    public bool Verified { get; set; }
    public string? Error { get; set; }
    public string Method { get; set; } = "";
}

public sealed class EmployeeDeleteResult
{
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public int Requested { get; set; }
    public int Deleted { get; set; }
    public int Failed { get; set; }
    public long ElapsedMs { get; set; }
    public IReadOnlyList<EmployeeSyncLineResult> Lines { get; set; } = [];
}

public sealed class DeviceEmployeeUploadResult
{
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public int Requested { get; set; }
    public int Uploaded { get; set; }
    public int Failed { get; set; }
    public long ElapsedMs { get; set; }
    public IReadOnlyList<EmployeeSyncLineResult> Lines { get; set; } = [];
}