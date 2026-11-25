using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Web.Authorization;
using BiometricDashboard.Web.Models;
using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BiometricDashboard.Web.Controllers;

[Authorize]
[RequirePermission("dashboard.view")]
public class DashboardController : Controller
{
    private readonly IAttendanceRepository _repository;
    private readonly IDeviceSyncService _sync;
    private readonly SelectedDeviceService _selection;
    private readonly IDeviceRepository _devices;
    private readonly DeviceOptions _deviceOptions;

    public DashboardController(
        IAttendanceRepository repository,
        IDeviceSyncService sync,
        SelectedDeviceService selection,
        IDeviceRepository devices,
        IOptions<DeviceOptions> deviceOptions)
    {
        _repository = repository;
        _sync = sync;
        _selection = selection;
        _devices = devices;
        _deviceOptions = deviceOptions.Value;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Overview";
        ViewData["GateosDashboard"] = true;
        ViewData["GateosOverview"] = true;
        ViewBag.SyncMode = _deviceOptions.SyncMode;
        ViewBag.DashboardPollSeconds = _deviceOptions.DashboardPollSeconds;
        ViewBag.DisplayName = HttpContext.Session.GetString("DisplayName") ?? User.Identity?.Name ?? "User";
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> OverviewData(CancellationToken cancellationToken)
    {
        var deviceList = await _devices.ListDevicesAsync(activeOnly: true, cancellationToken);
        var from = DateTime.Today.AddDays(-30);
        var recent = await _repository.GetRecentAsync(15000, null, cancellationToken);
        var punches = recent
            .Where(r => r.PunchTime >= from)
            .OrderByDescending(r => r.PunchTime)
            .Select(r => new
            {
                punchTime = r.PunchTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                deviceId = r.BioDeviceId,
                deviceName = r.DeviceName,
                r.EmployeeId,
                r.EmployeeName,
                r.Status,
                r.WorkCode,
                r.CardSource,
            })
            .ToList();

        var devices = deviceList.Select(d =>
        {
            var snap = _sync.GetSnapshot(d.DeviceId);
            return new
            {
                deviceId = d.DeviceId,
                name = d.DeviceName,
                ip = d.DeviceIp,
                online = snap?.IsOnline == true,
                enrolled = snap?.EmployeeCount ?? 0,
                status = snap?.IsOnline == true
                    ? "Online"
                    : snap?.LastError?.Contains("Push", StringComparison.OrdinalIgnoreCase) == true
                        ? "Push idle"
                        : "Inactive",
            };
        }).ToList();

        var total = await _repository.CountAsync(null, cancellationToken);
        return Json(new
        {
            ok = true,
            total,
            devices,
            punches,
            hasLiveData = punches.Count > 0,
        });
    }

    [HttpGet]
    public async Task<IActionResult> RecentJson(CancellationToken cancellationToken)
    {
        var selected = await _selection.GetSelectedDeviceAsync(cancellationToken);
        var rows = await _repository.GetRecentAsync(200, selected?.DeviceId, cancellationToken);
        var total = await _repository.CountAsync(selected?.DeviceId, cancellationToken);
        return Json(new
        {
            total,
            rows = rows.Select(r => new
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
            }),
        });
    }

    [HttpGet]
    public async Task<IActionResult> SyncNow(bool deviceInfo = false, CancellationToken cancellationToken = default)
    {
        var selected = await _selection.GetSelectedDeviceAsync(cancellationToken);
        if (selected is null)
            return Json(new { ok = false, message = "No device selected" });

        DevicePullSyncResult pull;
        if (_deviceOptions.IsPushMode)
            pull = new DevicePullSyncResult { Snapshot = _sync.GetSnapshot(selected.DeviceId) };
        else
            pull = await _sync.PullOnceAsync(selected.DeviceId, deviceInfo, cancellationToken);

        var snap = pull.Snapshot;
        var total = await _repository.CountAsync(selected.DeviceId, cancellationToken);

        static object RowDto(AttendanceRecord r) => new
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
        };

        return Json(new
        {
            ok = true,
            total,
            addedCount = pull.Added.Count,
            newRows = pull.Added.Select(RowDto),
            online = snap?.IsOnline == true,
            serialNumber = snap?.SerialNumber,
            employeeCount = snap?.EmployeeCount ?? 0,
            faceEnrolled = snap?.FaceEnrolled ?? 0,
            recordCountOnDevice = snap?.RecordCountOnDevice ?? 0,
            lastError = snap?.LastError,
            busy = snap?.LastError?.Contains("Busy", StringComparison.OrdinalIgnoreCase) == true,
        });
    }

    [HttpGet]
    public async Task<IActionResult> DeviceStatus(CancellationToken cancellationToken)
    {
        var selected = await _selection.GetSelectedDeviceAsync(cancellationToken);
        var d = selected is not null ? _sync.GetSnapshot(selected.DeviceId) : _sync.LatestSnapshot;
        if (d == null) return Json(new { online = false });
        return Json(new
        {
            online = d.IsOnline,
            d.SerialNumber,
            d.Firmware,
            d.DeviceType,
            d.EmployeeCount,
            d.FaceEnrolled,
            d.RecordCountOnDevice,
            d.LastError,
            busy = d.LastError?.Contains("Busy", StringComparison.OrdinalIgnoreCase) == true,
            checkedAt = d.CheckedAt.ToString("O"),
        });
    }
}
