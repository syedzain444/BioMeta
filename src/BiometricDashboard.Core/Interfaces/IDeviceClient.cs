using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public interface IDeviceClient
{
    Task<DeviceSnapshot> GetDeviceSnapshotAsync(
        DeviceEndpoint endpoint,
        CancellationToken cancellationToken = default,
        int commandMaxAttempts = 2);
    Task<AttendancePullResult> PullAttendanceAsync(
        DeviceEndpoint endpoint,
        CancellationToken cancellationToken = default,
        int commandMaxAttempts = 2);
    Task<AttendancePullResult> PullAttendanceAsync(
        DeviceEndpoint endpoint,
        string startTime,
        string endTime,
        CancellationToken cancellationToken = default,
        int commandMaxAttempts = 2);
    Task<DeviceEmployeeRecord?> GetEmployeeAsync(DeviceEndpoint endpoint, string employeeId, CancellationToken cancellationToken = default);
    Task<DeviceEmployeeLookupResult> LookupEmployeeWithPhotoAsync(
        DeviceEndpoint endpoint,
        string employeeIdOrQuery,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches display photos (photo_data) for rows not yet in cache — uses GetEmployee.</summary>
    Task EnrichDisplayPhotosAsync(
        DeviceEndpoint endpoint,
        IEnumerable<DeviceEmployeeRecord> employees,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default);
    Task<DeviceEmployeePullResult> PullEmployeesAsync(
        DeviceEndpoint endpoint,
        IEnumerable<string> employeeIds,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetEmployeeIdsFromDeviceAttendanceAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetEmployeeIdsFromDeviceRecordsInRangeAsync(
        DeviceEndpoint endpoint,
        DateTime startLocal,
        DateTime endLocal,
        CancellationToken cancellationToken = default);
    Task<DeviceEmployeePullResult> PullAllEnrolledAsync(
        DeviceEndpoint endpoint,
        int delayMsBetweenCalls = 120,
        int pushWaitSeconds = 90,
        CancellationToken cancellationToken = default,
        bool enrichPhotos = true,
        bool fastRosterPull = false);

    /// <summary>Fills <c>face_data</c> on records that lack templates, using one HDCP session.</summary>
    Task EnrichFaceTemplatesAsync(
        DeviceEndpoint endpoint,
        IList<DeviceEmployeeRecord> employees,
        int delayMsBetweenCalls = 80,
        CancellationToken cancellationToken = default);

    Task<DeviceEmployeeUploadResult> UploadEmployeesAsync(
        DeviceEndpoint endpoint,
        Guid jobId,
        IReadOnlyList<DeviceEmployeeRecord> employees,
        int delayMsBetweenCalls = 150,
        int pushWaitSeconds = 120,
        CancellationToken cancellationToken = default,
        Action<DeviceEmployeeUploadProgress>? onProgress = null);

    Task<EmployeeDiffResult> ComparePortalAndDeviceAsync(
        DeviceEndpoint endpoint,
        IReadOnlyList<string> portalEmployeeIds,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default);

    Task VerifyEmployeesOnDeviceAsync(
        DeviceEndpoint endpoint,
        IList<EmployeeSyncLineResult> lines,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default);

    Task<EmployeeDeleteResult> DeleteEmployeesAsync(
        DeviceEndpoint endpoint,
        IEnumerable<string> employeeIds,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default);
}
