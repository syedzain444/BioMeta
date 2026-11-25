using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Web.Models;
using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BiometricDashboard.Web.ViewComponents;

public sealed class DeviceMenuViewComponent(
    IDeviceRepository devices,
    SelectedDeviceService selection,
    IDeviceSyncService sync) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (HttpContext.User.Identity?.IsAuthenticated != true)
            return View(new DeviceMenuViewModel());

        var list = await devices.ListDevicesAsync(activeOnly: true, HttpContext.RequestAborted);
        var selected = await selection.GetSelectedDeviceAsync(HttpContext.RequestAborted);

        var items = list.Select(d => new DeviceMenuItemViewModel
        {
            DeviceId = d.DeviceId,
            DeviceName = d.DeviceName,
            DeviceIp = d.DeviceIp,
            IsLive = IsDeviceLive(d, sync.GetSnapshot(d.DeviceId)),
            IsSelected = selected?.DeviceId == d.DeviceId,
        }).ToList();

        return View(new DeviceMenuViewModel
        {
            SelectedDeviceId = selected?.DeviceId,
            DeviceName = selected?.DeviceName ?? (items.Count > 0 ? "Select device" : "No device"),
            DeviceIp = selected?.DeviceIp ?? "",
            IsLive = selected is not null && IsDeviceLive(selected, sync.GetSnapshot(selected.DeviceId)),
            Devices = items,
        });
    }

    private static bool IsDeviceLive(BiometricDevice d, DeviceSnapshot? snapshot) =>
        Core.Utilities.DeviceConnectivity.IsRecentlyOnline(snapshot, d.LastSeenAt);
}
