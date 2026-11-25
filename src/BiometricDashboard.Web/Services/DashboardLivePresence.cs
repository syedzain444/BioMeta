using System.Collections.Concurrent;
using BiometricDashboard.Core.Interfaces;

namespace BiometricDashboard.Web.Services;

public sealed class DashboardLivePresence : IDashboardLivePresence
{
    private readonly ConcurrentDictionary<string, byte> _connections = new();

    public bool AnyViewer => !_connections.IsEmpty;

    internal void ViewerConnected(string connectionId) => _connections.TryAdd(connectionId, 0);

    internal void ViewerDisconnected(string connectionId) => _connections.TryRemove(connectionId, out _);
}
