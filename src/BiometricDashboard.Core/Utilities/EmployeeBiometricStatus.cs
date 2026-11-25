using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Utilities;

public static class EmployeeBiometricStatus
{
    public static BiometricEnrollmentStatus FromRecord(DeviceEmployeeRecord? record)
    {
        if (record is null)
            return BiometricEnrollmentStatus.Unknown;

        var check = (record.CheckType ?? "").Trim().ToLowerInvariant();
        if (record.FaceTemplateCount > 0 || check.Contains("face"))
            return BiometricEnrollmentStatus.FaceEnrolled;
        if (check.Contains("finger") || check.Contains("fp"))
            return BiometricEnrollmentStatus.FingerprintEnrolled;
        if (check.Contains("card") || !string.IsNullOrWhiteSpace(record.CardNumber))
            return BiometricEnrollmentStatus.CardOnly;
        if (record.FoundOnDevice)
            return BiometricEnrollmentStatus.NoTemplate;
        return BiometricEnrollmentStatus.Unknown;
    }

    public static string ToLabel(BiometricEnrollmentStatus status) => status switch
    {
        BiometricEnrollmentStatus.FaceEnrolled => "Face enrolled",
        BiometricEnrollmentStatus.FingerprintEnrolled => "Fingerprint",
        BiometricEnrollmentStatus.CardOnly => "Card only",
        BiometricEnrollmentStatus.NoTemplate => "On device · no template",
        BiometricEnrollmentStatus.Mixed => "Mixed",
        _ => "Unknown",
    };
}