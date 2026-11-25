using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.SignalR;

namespace BiometricDashboard.Web.Hubs;

public class AttendanceHub(DashboardLivePresence presence) : Hub
{
    public const string HubPath = "/hubs/attendance";

    public override Task OnConnectedAsync()
    {
        presence.ViewerConnected(Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.ViewerDisconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
