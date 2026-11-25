using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public interface IAttendanceRepository
{
    Task<IReadOnlyList<AttendanceRecord>> GetRecentAsync(int count, long? bioDeviceId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AttendanceRecord>> UpsertManyAsync(IEnumerable<AttendanceRecord> records, CancellationToken cancellationToken = default);
    Task<int> CountAsync(long? bioDeviceId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetDistinctEmployeeIdsAsync(long? bioDeviceId = null, int maxRows = 5000, CancellationToken cancellationToken = default);

    /// <summary>Earliest punch per employee ID (normalized match on TRIM/LTRIM).</summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetFirstPunchByEmployeeIdsAsync(
        IEnumerable<string> employeeIds,
        long? bioDeviceId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Employee IDs whose earliest portal punch falls in [startInclusive, endExclusive).</summary>
    Task<IReadOnlyList<string>> GetEmployeeIdsWithFirstPunchBetweenAsync(
        DateTime startInclusive,
        DateTime endExclusive,
        long? bioDeviceId = null,
        int maxRows = 10000,
        CancellationToken cancellationToken = default);
}
