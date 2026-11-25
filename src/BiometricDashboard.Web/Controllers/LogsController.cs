using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Web.Authorization;
using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BiometricDashboard.Web.Controllers;

[Authorize]
[RequirePermission("logs.view")]
public class LogsController(
    IAttendanceLogQuery query,
    IAttendanceRepository attendance,
    IDeviceClient deviceClient,
    IDeviceRepository devices,
    SelectedDeviceService selection,
    IOptions<DeviceOptions> deviceOptions) : Controller
{
    private readonly DeviceOptions _opts = deviceOptions.Value;

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var device = await selection.GetSelectedDeviceAsync(cancellationToken);
        ViewBag.DeviceLabel = device is not null
            ? $"{device.DeviceName} ({device.DeviceIp})"
            : "No device selected";
        ViewBag.DeviceId = device?.DeviceId;
        ViewBag.SyncMode = _opts.SyncMode;

        var deviceId = device?.DeviceId;
        var total = await attendance.CountAsync(deviceId, cancellationToken);
        var today = DateTime.Today;
        var todayPage = await query.QueryAsync(
            new AttendanceLogQuery(today, today.AddDays(1).AddTicks(-1), null, null, null, 0, 1, deviceId),
            cancellationToken);

        ViewBag.PageBand = new BiometricDashboard.Web.Models.PortalPageBandViewModel
        {
            Eyebrow = "Attendance",
            Title = "Attendance logs",
            IconClass = "bi-journal-text",
            Subtitle = $"Device: <strong>{ViewBag.DeviceLabel}</strong>",
            SubtitleMuted = "Portal DB · device memory (9922) · live push/sync",
            Stats =
            [
                new() { Label = "Portal DB", Value = total.ToString(), Meta = "synced records", IconClass = "bi-database", ColorIndex = 1 },
                new() { Label = "Today", Value = todayPage.Total.ToString(), Meta = "in Oracle", IconClass = "bi-calendar-day", ColorIndex = 2 },
                new() { Label = "Terminal", Value = device?.DeviceName ?? "—", Meta = device?.DeviceIp ?? "select in top bar", IconClass = "bi-hdd-network", ColorIndex = 3 },
                new() { Label = "Mode", Value = _opts.IsPushMode ? "Push" : "Pull", Meta = "sync strategy", IconClass = "bi-broadcast", ColorIndex = 4 },
            ],
        };

        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Data(
        int draw,
        int start,
        int length,
        string? fromDate,
        string? toDate,
        string? fromTime,
        string? toTime,
        long? deviceId,
        [FromQuery(Name = "search[value]")] string? search,
        CancellationToken cancellationToken)
    {
        DateTime? from = ParseDate(fromDate);
        DateTime? to = ParseDate(toDate);
        if (to.HasValue)
            to = to.Value.Date.AddDays(1).AddTicks(-1);

        var filterDeviceId = await ResolveDeviceIdAsync(deviceId, cancellationToken);

        var page = await query.QueryAsync(
            new AttendanceLogQuery(from, to, ParseTime(fromTime), ParseTime(toTime), search, start, length <= 0 ? 25 : length, filterDeviceId),
            cancellationToken);

        return Json(new
        {
            draw,
            recordsTotal = page.Total,
            recordsFiltered = page.Total,
            data = page.Rows.Select(ToRowArray),
        });
    }

    [HttpGet]
    [RequestTimeout(milliseconds: 180_000)]
    public async Task<IActionResult> DeviceData(
        string? fromDate,
        string? toDate,
        string? fromTime,
        string? toTime,
        long? deviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var device = await ResolveDeviceAsync(deviceId, cancellationToken);
            if (device is null)
                return Json(new { ok = false, error = "Select a device (top bar Active device)." });

            var from = ParseDate(fromDate) ?? ParseDateTime(_opts.RecordStart)?.Date ?? DateTime.Today.AddMonths(-1);
            var toEnd = ParseDate(toDate) ?? DateTime.Today;
            var toInclusive = toEnd.Date.AddDays(1).AddTicks(-1);

            var endpoint = new DeviceEndpoint
            {
                DeviceId = device.DeviceId,
                Host = device.DeviceIp,
                Port = device.DevicePort,
                RecordStart = from.ToString("yyyy-MM-dd HH:mm:ss"),
                RecordEnd = toInclusive.ToString("yyyy-MM-dd HH:mm:ss"),
            };

            var pull = await deviceClient.PullAttendanceAsync(endpoint, cancellationToken);
            if (!pull.Succeeded)
            {
                return Json(new
                {
                    ok = false,
                    error = "GetRecord failed — check 9922, device busy, or date range.",
                    deviceName = device.DeviceName,
                });
            }

            var fromTod = ParseTime(fromTime);
            var toTod = ParseTime(toTime);
            var rows = pull.Records
                .Select(r =>
                {
                    r.BioDeviceId = device.DeviceId;
                    r.DeviceName = device.DeviceName;
                    r.DeviceLocation = device.DeviceLocation ?? "";
                    if (string.IsNullOrEmpty(r.DeviceSerial))
                        r.DeviceSerial = pull.DeviceSerial ?? "";
                    return r;
                })
                .Where(r => !fromTod.HasValue || r.PunchTime.TimeOfDay >= fromTod.Value)
                .Where(r => !toTod.HasValue || r.PunchTime.TimeOfDay <= toTod.Value)
                .OrderByDescending(r => r.PunchTime)
                .ToList();

            return Json(new
            {
                ok = true,
                deviceName = device.DeviceName,
                deviceIp = device.DeviceIp,
                totalOnDevice = pull.RecordTotalOnDevice,
                loaded = rows.Count,
                data = rows.Select(ToRowArray),
            });
        }
        catch (OperationCanceledException)
        {
            return Json(new { ok = false, error = "Request timed out or was cancelled while reading the device (GetRecord can take up to 3 minutes)." });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> LiveRecent(long? deviceId, int count = 150, CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 500);
        var filterDeviceId = await ResolveDeviceIdAsync(deviceId, cancellationToken);
        var rows = await attendance.GetRecentAsync(count, filterDeviceId, cancellationToken);
        var total = await attendance.CountAsync(filterDeviceId, cancellationToken);
        return Json(new
        {
            ok = true,
            total,
            data = rows.Select(ToRowArray),
        });
    }

    private async Task<long?> ResolveDeviceIdAsync(long? deviceId, CancellationToken cancellationToken)
    {
        if (deviceId is > 0) return deviceId;
        var selected = await selection.GetSelectedDeviceAsync(cancellationToken);
        return selected?.DeviceId;
    }

    private async Task<BiometricDevice?> ResolveDeviceAsync(long? deviceId, CancellationToken cancellationToken)
    {
        if (deviceId is > 0)
            return await devices.GetByIdAsync(deviceId.Value, cancellationToken);
        return await selection.GetSelectedDeviceAsync(cancellationToken);
    }

    private static string[] ToRowArray(AttendanceRecord r) =>
    [
        r.PunchTime.ToString("yyyy-MM-dd HH:mm:ss"),
        r.EmployeeId,
        r.EmployeeName,
        r.DeviceName,
        r.DeviceLocation,
        r.BioDeviceId?.ToString() ?? "",
        r.Status,
        r.WorkCode,
        r.CardSource,
        r.DeviceSerial ?? "",
    ];

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, out var d) ? d.Date : null;

    private static DateTime? ParseDateTime(string? s) =>
        DateTime.TryParse(s, out var d) ? d : null;

    private static TimeSpan? ParseTime(string? s) =>
        TimeSpan.TryParse(s, out var t) ? t : null;
}
