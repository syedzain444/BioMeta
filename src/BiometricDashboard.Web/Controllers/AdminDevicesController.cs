using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Core.Utilities;
using BiometricDashboard.Infrastructure.Security;
using BiometricDashboard.Web.Authorization;
using BiometricDashboard.Web.Models;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BiometricDashboard.Web.Controllers;

[Route("Admin/Devices")]
[RequirePermission("devices.view")]
public sealed class AdminDevicesController(
    IDeviceRepository devices,
    DeviceSecretProtector secrets,
    ICurrentUserContext currentUser,
    IDeviceSyncService sync,
    IOptions<DeviceOptions> deviceOptions) : Controller
{
    private PortalServerPortsViewModel ServerPortsFromConfig()
    {
        var o = deviceOptions.Value;
        return new PortalServerPortsViewModel
        {
            SyncMode = o.SyncMode,
            PushServerEnabled = o.EnablePushServer && o.IsPushMode,
            BindAddress = o.PushBindAddress,
            CmdPort = o.CmdPushPort,
            HbpPort = o.HbpPushPort,
            HdcpPort = o.Port,
        };
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var list = await devices.ListDevicesAsync(cancellationToken: cancellationToken);
        return View(new DeviceListViewModel
        {
            ServerPorts = ServerPortsFromConfig(),
            Devices = list.Select(d =>
            {
                var snap = sync.GetSnapshot(d.DeviceId);
                return new DeviceRowViewModel
                {
                    DeviceId = d.DeviceId,
                    DeviceName = d.DeviceName,
                    DeviceIp = d.DeviceIp,
                    DevicePort = d.DevicePort,
                    DeviceLocation = d.DeviceLocation,
                    DeviceSerial = d.DeviceSerial ?? snap?.SerialNumber,
                    IsActive = d.IsActive,
                    IsPrimary = d.IsPrimary,
                    LastSeenAt = d.LastSeenAt,
                    LastSyncError = d.LastSyncError ?? snap?.LastError,
                    IsOnline = DeviceConnectivity.IsRecentlyOnline(snap, d.LastSeenAt),
                };
            }).ToList(),
        });
    }

    [RequirePermission("devices.manage")]
    [HttpPost("Test/{id:long}")]
    [ValidateAntiForgeryToken]
    [RequestTimeout(milliseconds: 180_000)]
    public Task<IActionResult> TestConnection(long id, CancellationToken cancellationToken) =>
        RunProbeJsonAsync(id, () => sync.ProbeDeviceAsync(id, cancellationToken), cancellationToken);

    [RequirePermission("devices.manage")]
    [HttpPost("TestDraft")]
    [ValidateAntiForgeryToken]
    [RequestTimeout(milliseconds: 180_000)]
    public Task<IActionResult> TestDraft(string deviceIp, int devicePort, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceIp))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "Device IP is required." }));
        return RunProbeJsonAsync(deviceId: null, () => sync.ProbeEndpointAsync(deviceIp, devicePort, cancellationToken), cancellationToken);
    }

    private async Task<IActionResult> RunProbeJsonAsync(
        long? deviceId,
        Func<Task<DeviceConnectionTestResult>> probe,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await probe();
            var snap = result.Snapshot;
            return new JsonResult(new
            {
                ok = result.Ok,
                online = result.Online,
                dataOk = result.DataChannelOk,
                tcpOk = result.TcpReachable,
                tcpMs = result.TcpLatencyMs,
                tcpError = result.TcpError,
                host = result.Host,
                port = result.Port,
                message = result.Message,
                error = snap?.LastError,
                serial = snap?.SerialNumber,
                employees = snap?.EmployeeCount ?? 0,
                faceEnrolled = snap?.FaceEnrolled ?? 0,
                recordsOnDevice = result.TotalRecordsOnDevice,
                sampleRecords = result.SampleRecordCount,
                recordBuffer = snap?.RecordCountOnDevice,
                portalAttendanceRows = result.PortalAttendanceRows,
                probeWindowStart = result.ProbeWindowStart,
                probeWindowEnd = result.ProbeWindowEnd,
                syncMode = deviceOptions.Value.SyncMode,
                firmware = snap?.Firmware,
                checkedAt = snap?.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            });
        }
        catch (Exception ex)
        {
            if (deviceId is long id)
            {
                try
                {
                    await sync.RecordProbeFailureAsync(id, ex.Message, cancellationToken);
                }
                catch
                {
                    // still return JSON to the client
                }
            }

            var checkedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            return new JsonResult(new
            {
                ok = false,
                online = false,
                dataOk = false,
                tcpOk = false,
                message = "Connection test error on server.",
                error = ex.Message,
                checkedAt,
            });
        }
    }

    private bool WantsJsonSave() =>
        Request.Headers.TryGetValue("Accept", out var accept) &&
        accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    private IActionResult SaveValidationFailed(DeviceFormViewModel model)
    {
        if (WantsJsonSave())
        {
            var errors = ModelState
                .Where(kv => kv.Value?.Errors.Count > 0)
                .ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value!.Errors.Select(e => e.ErrorMessage).ToArray());
            return new JsonResult(new { ok = false, message = "Fix the highlighted fields.", errors })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }

        ViewBag.ServerPorts = ServerPortsFromConfig();
        return View("Edit", model);
    }

    private object ClientDeviceJson(
        long id,
        DeviceFormViewModel model,
        string? serial,
        bool? isOnline = null)
    {
        var snap = sync.GetSnapshot(id);
        var online = isOnline ?? DeviceConnectivity.IsRecentlyOnline(snap, null);
        return new
        {
            id,
            name = model.DeviceName.Trim(),
            ip = model.DeviceIp.Trim(),
            port = model.DevicePort,
            loc = model.DeviceLocation.Trim(),
            serial = string.IsNullOrEmpty(serial) ? "—" : serial,
            primary = model.IsPrimary ? "Yes" : "—",
            online,
            active = model.IsActive,
            lastSyncError = snap?.LastError ?? "",
            editUrl = Url.Action(nameof(FormModal), new { id }),
            deleteUrl = Url.Action(nameof(Delete), new { id }),
        };
    }

    [RequirePermission("devices.manage")]
    [HttpGet("Create")]
    public IActionResult Create() => RedirectToAction(nameof(Index), new { create = 1 });

    [RequirePermission("devices.manage")]
    [HttpGet("Edit/{id:long}")]
    public IActionResult Edit(long id) => RedirectToAction(nameof(Index), new { open = id });

    [RequirePermission("devices.manage")]
    [HttpGet("Form/{id:long?}")]
    public async Task<IActionResult> FormModal(long? id, CancellationToken cancellationToken)
    {
        ViewBag.ServerPorts = ServerPortsFromConfig();
        if (id is null or 0)
        {
            var o = deviceOptions.Value;
            return PartialView("_DeviceFormModal", new DeviceFormViewModel
            {
                IsPrimary = false,
                IsActive = true,
                DevicePort = o.Port > 0 ? o.Port : 9922,
            });
        }

        var d = await devices.GetByIdAsync(id.Value, cancellationToken);
        if (d is null) return NotFound();
        return PartialView("_DeviceFormModal", new DeviceFormViewModel
        {
            DeviceId = d.DeviceId,
            DeviceIp = d.DeviceIp,
            DevicePort = d.DevicePort,
            AdminId = d.AdminId ?? "",
            DeviceName = d.DeviceName,
            DeviceLocation = d.DeviceLocation ?? "",
            IsActive = d.IsActive,
            IsPrimary = d.IsPrimary,
        });
    }

    [RequirePermission("devices.manage")]
    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(DeviceFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return SaveValidationFailed(model);

        var actor = currentUser.UserId;
        string? serialFromTest = null;
        if (!string.IsNullOrWhiteSpace(model.LastTestSerial))
            serialFromTest = model.LastTestSerial.Trim();

        if (model.DeviceId is null or 0)
        {
            if (string.IsNullOrWhiteSpace(model.AdminPassword))
            {
                ModelState.AddModelError(nameof(model.AdminPassword), "Password required for new device.");
                return SaveValidationFailed(model);
            }

            var newId = await devices.CreateAsync(new BiometricDevice
            {
                DeviceIp = model.DeviceIp.Trim(),
                DevicePort = model.DevicePort,
                AdminId = model.AdminId.Trim(),
                AdminPasswordProtected = secrets.Protect(model.AdminPassword),
                DeviceName = model.DeviceName.Trim(),
                DeviceLocation = model.DeviceLocation.Trim(),
                DeviceSerial = serialFromTest,
                IsActive = model.IsActive,
                IsPrimary = model.IsPrimary,
            }, actor, cancellationToken);

            var createMsg = serialFromTest is not null
                ? "Device saved. Connection test result was applied."
                : "Device registered.";
            if (WantsJsonSave())
            {
                return new JsonResult(new
                {
                    ok = true,
                    message = createMsg,
                    device = ClientDeviceJson(newId, model, serialFromTest, isOnline: false),
                });
            }

            TempData["Success"] = createMsg;
            return RedirectToAction(nameof(Index));
        }

        var existing = await devices.GetByIdAsync(model.DeviceId.Value, cancellationToken);
        if (existing is null)
        {
            if (WantsJsonSave())
                return new JsonResult(new { ok = false, message = "Device not found." }) { StatusCode = 404 };
            return NotFound();
        }

        existing.DeviceIp = model.DeviceIp.Trim();
        existing.DevicePort = model.DevicePort;
        existing.AdminId = model.AdminId.Trim();
        if (!string.IsNullOrWhiteSpace(model.AdminPassword))
            existing.AdminPasswordProtected = secrets.Protect(model.AdminPassword);
        existing.DeviceName = model.DeviceName.Trim();
        existing.DeviceLocation = model.DeviceLocation.Trim();
        if (serialFromTest is not null)
            existing.DeviceSerial = serialFromTest;
        existing.IsActive = model.IsActive;
        existing.IsPrimary = model.IsPrimary;
        await devices.UpdateAsync(existing, actor, cancellationToken);

        var serial = serialFromTest ?? existing.DeviceSerial;
        if (WantsJsonSave())
        {
            return new JsonResult(new
            {
                ok = true,
                message = "Device updated.",
                device = ClientDeviceJson(existing.DeviceId, model, serial),
            });
        }

        TempData["Success"] = "Device updated.";
        return RedirectToAction(nameof(Index));
    }

    [RequirePermission("devices.manage")]
    [HttpPost("Delete/{id:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await devices.SoftDeleteAsync(id, currentUser.UserId ?? 1, cancellationToken);
        TempData["Success"] = "Device removed.";
        return RedirectToAction(nameof(Index));
    }
}
