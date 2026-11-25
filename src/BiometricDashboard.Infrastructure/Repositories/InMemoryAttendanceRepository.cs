using System.Collections.Concurrent;
using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Utilities;

namespace BiometricDashboard.Infrastructure.Repositories;

public sealed class InMemoryAttendanceRepository : IAttendanceRepository
{
    private readonly ConcurrentDictionary<string, AttendanceRecord> _byKey = new();
    private readonly object _orderLock = new();

    public Task<int> CountAsync(long? bioDeviceId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byKey.Count);

    public Task<IReadOnlyList<string>> GetDistinctEmployeeIdsAsync(
        long? bioDeviceId = null,
        int maxRows = 5000,
        CancellationToken cancellationToken = default)
    {
        var ids = _byKey.Values
            .Where(r => bioDeviceId is null or 0 || r.BioDeviceId == bioDeviceId)
            .Select(r => r.EmployeeId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(maxRows)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    public Task<IReadOnlyList<AttendanceRecord>> GetRecentAsync(int count, long? bioDeviceId = null, CancellationToken cancellationToken = default)
    {
        var items = _byKey.Values
            .OrderByDescending(r => r.PunchTime)
            .Take(count)
            .ToList();
        return Task.FromResult<IReadOnlyList<AttendanceRecord>>(items);
    }

    public Task<IReadOnlyList<AttendanceRecord>> UpsertManyAsync(
        IEnumerable<AttendanceRecord> records,
        CancellationToken cancellationToken = default)
    {
        var added = new List<AttendanceRecord>();
        lock (_orderLock)
        {
            foreach (var record in records)
            {
                if (_byKey.TryAdd(record.DedupeKey, record))
                    added.Add(record);
            }
        }
        return Task.FromResult<IReadOnlyList<AttendanceRecord>>(added);
    }

    public Task<IReadOnlyDictionary<string, DateTime>> GetFirstPunchByEmployeeIdsAsync(
        IEnumerable<string> employeeIds,
        long? bioDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        var want = new HashSet<string>(
            employeeIds.Select(NicIdNormalizer.Normalize).Where(x => !string.IsNullOrEmpty(x)),
            StringComparer.Ordinal);
        var map = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var r in _byKey.Values)
        {
            if (bioDeviceId is > 0 && r.BioDeviceId != bioDeviceId) continue;
            var id = NicIdNormalizer.Normalize(r.EmployeeId);
            if (string.IsNullOrEmpty(id) || !want.Contains(id)) continue;
            if (!map.TryGetValue(id, out var cur) || r.PunchTime < cur)
                map[id] = r.PunchTime;
        }

        return Task.FromResult<IReadOnlyDictionary<string, DateTime>>(map);
    }

    public Task<IReadOnlyList<string>> GetEmployeeIdsWithFirstPunchBetweenAsync(
        DateTime startInclusive,
        DateTime endExclusive,
        long? bioDeviceId = null,
        int maxRows = 10000,
        CancellationToken cancellationToken = default)
    {
        var first = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var r in _byKey.Values)
        {
            if (bioDeviceId is > 0 && r.BioDeviceId != bioDeviceId) continue;
            var id = NicIdNormalizer.Normalize(r.EmployeeId);
            if (string.IsNullOrEmpty(id)) continue;
            if (!first.TryGetValue(id, out var cur) || r.PunchTime < cur)
                first[id] = r.PunchTime;
        }

        var ids = first
            .Where(kv => kv.Value >= startInclusive && kv.Value < endExclusive)
            .Select(kv => kv.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(maxRows)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }
}
