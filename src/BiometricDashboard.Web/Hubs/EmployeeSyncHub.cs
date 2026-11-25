using Microsoft.AspNetCore.SignalR;

namespace BiometricDashboard.Web.Hubs;

public sealed class EmployeeSyncHub : Hub
{
    public const string HubPath = "/hubs/employee-sync";

    public Task SubscribeJob(string jobId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, jobId);

    public Task UnsubscribeJob(string jobId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
}