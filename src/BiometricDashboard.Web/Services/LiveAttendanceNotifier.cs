using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BiometricDashboard.Web.Services;

public sealed class LiveAttendanceNotifier : IHostedService
{
    private readonly IDeviceSyncService _sync;
    private readonly IHubContext<AttendanceHub> _hub;

    public LiveAttendanceNotifier(IDeviceSyncService sync, IHubContext<AttendanceHub> hub)
    {
        _sync = sync;
        _hub = hub;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sync.NewRecordsReceived += OnNewRecords;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sync.NewRecordsReceived -= OnNewRecords;
        return Task.CompletedTask;
    }

    private async Task OnNewRecords(IReadOnlyList<AttendanceRecord> records)
    {
        var payload = records.Select(r => new
        {
            r.EmployeeId,
            r.EmployeeName,
            punchTime = r.PunchTime.ToString("yyyy-MM-dd HH:mm:ss"),
            r.DeviceName,
            r.DeviceLocation,
            deviceId = r.BioDeviceId,
            r.Status,
            r.WorkCode,
            r.CardSource,
            r.DeviceSerial,
        }).ToList();

        await _hub.Clients.All.SendAsync("NewPunches", payload);
    }
}
