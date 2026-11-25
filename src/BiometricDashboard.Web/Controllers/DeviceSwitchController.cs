using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BiometricDashboard.Web.Controllers;

[Authorize]
public sealed class DeviceSwitchController(
    SelectedDeviceService selection,
    IDeviceRepository devices) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Select(long deviceId, string? returnUrl, CancellationToken cancellationToken)
    {
        var d = await devices.GetByIdAsync(deviceId, cancellationToken);
        if (d is not null && d.IsActive)
            selection.SetSelectedDeviceId(deviceId);

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Dashboard");
    }
}
