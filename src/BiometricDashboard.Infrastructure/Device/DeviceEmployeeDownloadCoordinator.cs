using System.Collections.Concurrent;
using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Infrastructure.Device;

/// <summary>
/// While an upload job is active, serves <c>Employee(...)</c> payloads on device <c>GetRequest</c> (push port).
/// </summary>
public sealed class DeviceEmployeeDownloadCoordinator
{
    private readonly ConcurrentDictionary<string, UploadSession> _byIp = new(StringComparer.OrdinalIgnoreCase);

    private sealed class UploadSession
    {
        public required Guid JobId { get; init; }
        public required Queue<string> Commands { get; init; }
        public int Total { get; init; }
        public int Sent { get; set; }
        public int Acknowledged { get; set; }
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    }

    public IDisposable BeginUpload(string deviceIp, Guid jobId, IEnumerable<string> employeeCommands)
    {
        var list = employeeCommands.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var session = new UploadSession
        {
            JobId = jobId,
            Commands = new Queue<string>(list),
            Total = list.Count,
        };
        _byIp[deviceIp.Trim()] = session;
        return new Handle(deviceIp.Trim(), this);
    }

    public bool TryTakeNextCommand(string peerIp, out string command, out UploadProgressSnapshot? progress)
    {
        command = "";
        progress = null;
        if (string.IsNullOrWhiteSpace(peerIp)) return false;
        var session = ResolveSessionForPeer(peerIp);
        if (session is null || session.Commands.Count == 0) return false;

        command = session.Commands.Dequeue();
        session.Sent++;
        progress = ToSnapshot(session);
        return true;
    }

    public void Acknowledge(string peerIp)
    {
        var session = ResolveSessionForPeer(peerIp);
        if (session is null) return;
        session.Acknowledged++;
    }

    /// <summary>
    /// Push TCP peer may differ from HDCP host (e.g. .202 vs .203). Exact key first, else single active upload session.
    /// </summary>
    private UploadSession? ResolveSessionForPeer(string peerIp)
    {
        var peer = peerIp.Trim();
        if (_byIp.TryGetValue(peer, out var exact))
            return exact;

        var pending = _byIp.Values.Where(s => s.Commands.Count > 0 || s.Sent > s.Acknowledged).ToList();
        return pending.Count == 1 ? pending[0] : null;
    }

    public UploadProgressSnapshot? GetProgress(string deviceIp)
    {
        return _byIp.TryGetValue(deviceIp.Trim(), out var session) ? ToSnapshot(session) : null;
    }

    public void EndUpload(string deviceIp) => _byIp.TryRemove(deviceIp.Trim(), out _);

    private static UploadProgressSnapshot ToSnapshot(UploadSession s) => new()
    {
        JobId = s.JobId,
        Total = s.Total,
        Sent = s.Sent,
        Acknowledged = s.Acknowledged,
        Remaining = s.Commands.Count,
    };

    private sealed class Handle(string ip, DeviceEmployeeDownloadCoordinator owner) : IDisposable
    {
        public void Dispose() => owner.EndUpload(ip);
    }

    public sealed class UploadProgressSnapshot
    {
        public Guid JobId { get; init; }
        public int Total { get; init; }
        public int Sent { get; init; }
        public int Acknowledged { get; init; }
        public int Remaining { get; init; }
    }
}