using System.Collections.Concurrent;
using System.Net;
using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Utilities;

namespace BiometricDashboard.Infrastructure.Device;

/// <summary>
/// Collects <c>Employee(...)</c> push payloads while a 9922 SyncEmployee / upload is in progress.
/// </summary>
public sealed class DevicePushSyncCoordinator
{
    private readonly ConcurrentDictionary<long, SyncSession> _sessions = new();

    private sealed class SyncSession : IDisposable
    {
        public required string DeviceIp { get; init; }
        public int TargetCount { get; init; }
        public ConcurrentDictionary<string, DeviceEmployeeRecord> ById { get; } = new(StringComparer.Ordinal);
        public DateTime LastReceivedUtc { get; set; } = DateTime.UtcNow;

        public void Dispose()
        {
        }
    }

    public IDisposable BeginSession(long deviceId, string deviceIp, int targetCount = 0)
    {
        var session = new SyncSession
        {
            DeviceIp = deviceIp.Trim(),
            TargetCount = targetCount,
        };
        _sessions[deviceId] = session;
        return new SessionHandle(deviceId, this);
    }

    public void EndSession(long deviceId) => _sessions.TryRemove(deviceId, out _);

    internal static string NormalizePeerIp(string? peerIp)
    {
        if (string.IsNullOrWhiteSpace(peerIp)) return "";
        var trimmed = peerIp.Trim();
        if (IPAddress.TryParse(trimmed, out var addr))
            return addr.MapToIPv4().ToString();
        return trimmed;
    }

    public void TryAddEmployee(string peerIp, DeviceEmployeeRecord employee)
    {
        if (string.IsNullOrWhiteSpace(peerIp) || employee is null) return;
        var id = NicIdNormalizer.Normalize(employee.EmployeeId);
        if (string.IsNullOrEmpty(id)) return;
        employee.EmployeeId = id;
        var remote = NormalizePeerIp(peerIp);

        foreach (var kv in _sessions)
        {
            if (!string.Equals(NormalizePeerIp(kv.Value.DeviceIp), remote, StringComparison.OrdinalIgnoreCase))
                continue;
            kv.Value.ById[id] = employee;
            kv.Value.LastReceivedUtc = DateTime.UtcNow;
            break;
        }
    }

    public IReadOnlyList<DeviceEmployeeRecord> TakeEmployees(long deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var session))
            return [];
        return session.ById.Values.OrderBy(e => e.EmployeeId, StringComparer.Ordinal).ToList();
    }

    public async Task WaitForEmployeesAsync(
        long deviceId,
        int targetCount,
        TimeSpan maxWait,
        CancellationToken cancellationToken)
    {
        if (!_sessions.ContainsKey(deviceId))
            return;

        var deadline = DateTime.UtcNow + maxWait;
        var stableSince = DateTime.UtcNow;
        var lastCount = -1;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessions.TryGetValue(deviceId, out var session))
                break;

            var count = session.ById.Count;
            if (targetCount > 0 && count >= targetCount)
                break;

            if (count == lastCount)
            {
                if (DateTime.UtcNow - stableSince > TimeSpan.FromSeconds(8) && count > 0)
                    break;
            }
            else
            {
                lastCount = count;
                stableSince = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - session.LastReceivedUtc > TimeSpan.FromSeconds(12) && count > 0)
                break;

            await Task.Delay(400, cancellationToken);
        }
    }

    private sealed class SessionHandle(long deviceId, DevicePushSyncCoordinator owner) : IDisposable
    {
        public void Dispose() => owner.EndSession(deviceId);
    }
}
