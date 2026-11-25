using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Web.Models;
using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BiometricDashboard.Web.ViewComponents;

public sealed class DeviceSwitcherViewComponent(IDeviceRepository devices, SelectedDeviceService selection) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (HttpContext.User.Identity?.IsAuthenticated != true)
            return View(new DeviceSwitcherViewModel());

        var list = await devices.ListDevicesAsync(activeOnly: true, HttpContext.RequestAborted);
        var selected = await selection.GetSelectedDeviceAsync(HttpContext.RequestAborted);
        return View(new DeviceSwitcherViewModel
        {
            SelectedDeviceId = selected?.DeviceId,
            Devices = list.Select(d => new DeviceOptionViewModel
            {
                DeviceId = d.DeviceId,
                Label = $"{d.DeviceName} ({d.DeviceIp})",
            }).ToList(),
        });
    }
}
