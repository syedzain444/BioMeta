using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public record AttendanceLogQuery(
    DateTime? From,
    DateTime? To,
    TimeSpan? FromTimeOfDay,
    TimeSpan? ToTimeOfDay,
    string? Search,
    int Skip,
    int Take,
    long? BioDeviceId = null);

public record AttendanceLogPage(int Total, IReadOnlyList<AttendanceRecord> Rows);

public interface IAttendanceLogQuery
{
    Task<AttendanceLogPage> QueryAsync(AttendanceLogQuery query, CancellationToken cancellationToken = default);
}
