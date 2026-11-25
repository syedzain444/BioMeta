using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Infrastructure.Security;
using BiometricDashboard.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BiometricDashboard.Web.Controllers;

[AllowAnonymous]
public sealed class SetupController(
    IDeviceRepository devices,
    IDeviceClient deviceClient,
    DeviceSecretProtector secrets) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (await devices.IsSetupCompleteAsync(cancellationToken))
            return RedirectToAction("Login", "Account");

        return View(new DeviceFormViewModel { IsInitialSetup = true, IsPrimary = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(DeviceFormViewModel model, CancellationToken cancellationToken)
    {
        model.IsInitialSetup = true;
        if (!ModelState.IsValid)
            return View(model);

        if (string.IsNullOrWhiteSpace(model.AdminPassword))
        {
            ModelState.AddModelError(nameof(model.AdminPassword), "Device admin password is required.");
            return View(model);
        }

        var endpoint = new DeviceEndpoint
        {
            Host = model.DeviceIp.Trim(),
            Port = model.DevicePort,
        };
        var snap = await deviceClient.GetDeviceSnapshotAsync(endpoint, cancellationToken);
        if (!snap.IsOnline)
        {
            ModelState.AddModelError(string.Empty, snap.LastError ?? "Could not connect to device. Check IP/port and network.");
            return View(model);
        }

        var entity = new BiometricDevice
        {
            DeviceIp = model.DeviceIp.Trim(),
            DevicePort = model.DevicePort,
            AdminId = model.AdminId.Trim(),
            AdminPasswordProtected = secrets.Protect(model.AdminPassword),
            DeviceName = model.DeviceName.Trim(),
            DeviceLocation = model.DeviceLocation.Trim(),
            DeviceSerial = snap.SerialNumber,
            IsActive = true,
            IsPrimary = true,
        };

        await devices.CreateAsync(entity, null, cancellationToken);
        await devices.MarkSetupCompleteAsync(cancellationToken);

        TempData["Success"] = "Device configured. Sign in to open the portal.";
        return RedirectToAction("Login", "Account");
    }
}
