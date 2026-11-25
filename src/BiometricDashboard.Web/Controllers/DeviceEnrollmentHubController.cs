using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Core.Utilities;
using BiometricDashboard.Web.Authorization;
using BiometricDashboard.Web.Models;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BiometricDashboard.Web.Controllers;

[Route("Device/EnrollmentHub")]
[RequirePermission("device_enroll_hub.view")]
public sealed class DeviceEnrollmentHubController(
    IDeviceRepository devices,
    IDeviceClient deviceClient,
    IAttendanceRepository attendance,
    IEmployeeRegistryRepository employeeRegistry,
    IDeviceEnrollmentRepository enrollmentStore,
    ICurrentUserContext currentUser,
    IOptions<DeviceOptions> deviceOptions) : Controller
{
    private readonly DeviceOptions _opts = deviceOptions.Value;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var all = await devices.ListDevicesAsync(activeOnly: true, cancellationToken);
        ViewBag.Devices = all;
        ViewBag.PageBand = new PortalPageBandViewModel
        {
            Eyebrow = "Multi-device",
            Title = "Enrollment hub",
            IconClass = "bi-layers",
            SubtitleMuted = "Fetch selected or all devices · compare by employee ID · save to BIO_DEVICE_ENROLLMENT",
            Stats =
            [
                new()
                {
                    Label = "Active devices",
                    Value = all.Count.ToString(),
                    Meta = "in portal",
                    IconClass = "bi-diagram-3",
                    ColorIndex = 1,
                },
            ],
        };
        ViewBag.CanSave = currentUser.HasPermission("device_enroll_hub.save");
        return View();
    }

    [HttpGet("Devices")]
    public async Task<IActionResult> DeviceList(CancellationToken cancellationToken)
    {
        var all = await devices.ListDevicesAsync(activeOnly: false, cancellationToken);
        return Json(new
        {
            ok = true,
            devices = all.Select(d => new
            {
                d.DeviceId,
                d.DeviceName,
                d.DeviceIp,
                d.DevicePort,
                d.IsActive,
            }),
        });
    }

    [HttpPost("FetchDevice")]
    [RequestTimeout(milliseconds: 900_000)]
    public async Task<IActionResult> FetchDevice([FromBody] HubFetchOneRequest body, CancellationToken cancellationToken)
    {
        if (body.DeviceId <= 0)
            return Json(new { ok = false, error = "Invalid device." });

        var dev = await devices.GetByIdAsync(body.DeviceId, cancellationToken);
        if (dev is null)
            return Json(new { ok = false, error = "Device not found." });

        var delay = body.DelayMs is > 0 and < 2000 ? body.DelayMs.Value : 120;
        var pushWait = body.PushWaitSeconds is > 0 and <= 300 ? body.PushWaitSeconds.Value : 120;
        using var hubPullTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hubPullTimeout.CancelAfter(TimeSpan.FromMinutes(12));
        var (hubRows, errors, enrolled, elapsed) = await PullOneDeviceAsync(
            dev, delay, pushWait, hubPullTimeout.Token, maxSupplementIds: 400);

        return Json(new
        {
            ok = hubRows.Count > 0,
            error = hubRows.Count > 0 ? null : errors.FirstOrDefault() ?? "No users on this device.",
            warnings = errors.Count > 0 ? errors : null,
            found = hubRows.Count,
            deviceId = dev.DeviceId,
            deviceName = dev.DeviceName,
            deviceEnrolledCount = enrolled,
            elapsedMs = elapsed,
            rows = hubRows,
        });
    }

    [HttpPost("Fetch")]
    [RequestTimeout(milliseconds: 900_000)]
    public async Task<IActionResult> Fetch([FromBody] HubFetchRequest body, CancellationToken cancellationToken)
    {
        var delay = body.DelayMs is > 0 and < 2000 ? body.DelayMs.Value : 120;
        var pushWait = body.PushWaitSeconds is > 0 and <= 300 ? body.PushWaitSeconds.Value : 120;
        var targets = await ResolveTargetDevicesAsync(body.DeviceIds, body.AllDevices, cancellationToken);
        if (targets.Count == 0)
            return Json(new { ok = false, error = "Select at least one device." });

        if (targets.Count == 1)
        {
            var one = targets[0];
            using var hubPullTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hubPullTimeout.CancelAfter(TimeSpan.FromMinutes(12));
            var (hubRows, errors, enrolled, elapsed) = await PullOneDeviceAsync(
                one, delay, pushWait, hubPullTimeout.Token, maxSupplementIds: 400);
            return Json(new
            {
                ok = hubRows.Count > 0,
                error = hubRows.Count > 0 ? null : errors.FirstOrDefault() ?? "No users.",
                warnings = errors.Count > 0 ? errors : null,
                found = hubRows.Count,
                deviceCount = 1,
                deviceEnrolledCount = enrolled,
                elapsedMs = elapsed,
                rows = hubRows,
            });
        }

        return Json(new
        {
            ok = false,
            error = "Select multiple devices in the UI — the hub fetches one device at a time to avoid timeouts.",
            usePerDevice = true,
            deviceIds = targets.Select(d => d.DeviceId).ToList(),
        });
    }

    [HttpPost("Compare")]
    public IActionResult Compare([FromBody] HubCompareRequest body)
    {
        if (body.Rows is null || body.Rows.Count == 0)
            return Json(new { ok = false, error = "Fetch data first." });

        var inputs = body.Rows.Select(r => new DeviceEnrollmentCompareRowInput
        {
            BioDeviceId = r.DeviceId,
            DeviceName = r.DeviceName,
            EmployeeId = r.EmployeeId ?? "",
            DisplayName = r.Name,
        });

        var result = enrollmentStore.CompareRows(inputs);
        return Json(new { ok = true, compare = result });
    }

    [HttpPost("SavePortal")]
    [RequirePermission("device_enroll_hub.save")]
    public async Task<IActionResult> SavePortal([FromBody] HubSaveRequest body, CancellationToken cancellationToken)
    {
        if (body.Rows is null || body.Rows.Count == 0)
            return Json(new { ok = false, error = "Nothing to save — fetch first." });

        var actor = currentUser.UserId;
        var byDevice = body.Rows
            .GroupBy(r => r.DeviceId)
            .Where(g => g.Key > 0)
            .ToList();

        var batchId = await enrollmentStore.SaveFetchBatchAsync(
            byDevice.Count,
            body.Rows.Count,
            actor,
            body.Note,
            cancellationToken);

        var saved = 0;
        foreach (var group in byDevice)
        {
            var records = group.Select(r => new DeviceEmployeeRecord
            {
                EmployeeId = r.EmployeeId ?? "",
                Name = r.Name ?? "",
                Department = r.Department,
                CardNumber = r.CardNumber,
                Authority = r.Authority,
                CheckType = r.CheckType,
                FaceTemplateCount = r.FaceTemplateCount ?? 0,
                BioDeviceId = group.Key,
                EnrolledAtUtc = ParseEnrolled(r.EnrolledAt),
                EnrolledAtSource = r.EnrolledAtSource,
            }).ToList();

            saved += await enrollmentStore.UpsertFromDevicePullAsync(batchId, group.Key, records, actor, cancellationToken);

            if (body.AlsoUpdateRegistry)
            {
                var importRows = records.Select((r, i) => new EmployeeImportRow
                {
                    Line = i + 1,
                    EmployeeId = r.EmployeeId,
                    Name = r.Name,
                    Department = r.Department,
                    CardNumber = r.CardNumber,
                    Authority = r.Authority,
                }).ToList();
                await employeeRegistry.UpsertManyAsync(importRows, group.Key, actor, "enrollment-hub", cancellationToken);
            }
        }

        return Json(new
        {
            ok = saved > 0,
            saved,
            batchId = batchId.ToString("N"),
            message = saved > 0
                ? $"Saved {saved} row(s) to portal (per device)."
                : "No rows saved — run migration 013_device_enrollment_hub.sql on Oracle.",
        });
    }

    [HttpGet("Stored")]
    public async Task<IActionResult> Stored([FromQuery] string? deviceIds, CancellationToken cancellationToken)
    {
        var ids = ParseIdList(deviceIds);
        if (ids.Count == 0)
            return Json(new { ok = false, error = "Select devices." });

        var rows = await enrollmentStore.ListByDevicesAsync(ids, cancellationToken);
        var devMap = (await devices.ListDevicesAsync(cancellationToken: cancellationToken))
            .ToDictionary(d => d.DeviceId);

        return Json(new
        {
            ok = true,
            count = rows.Count,
            rows = rows.Select(r =>
            {
                devMap.TryGetValue(r.BioDeviceId, out var dev);
                return new
                {
                    deviceId = r.BioDeviceId,
                    deviceName = dev?.DeviceName,
                    deviceIp = dev?.DeviceIp,
                    r.EmployeeId,
                    name = r.DisplayName,
                    r.Department,
                    r.CardNumber,
                    r.BiometricStatus,
                    enrolledAt = r.EnrolledAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                    lastFetchedAt = r.LastFetchedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                };
            }),
        });
    }

    private async Task<(List<object> Rows, List<string> Errors, int DeviceEnrolledCount, long ElapsedMs)> PullOneDeviceAsync(
        BiometricDevice dev,
        int delay,
        int pushWait,
        CancellationToken cancellationToken,
        int maxSupplementIds = 400)
    {
        var errors = new List<string>();
        var hubRows = new List<object>();
        var ep = ToEndpoint(dev);
        DeviceEmployeePullResult pull;
        try
        {
            pull = await deviceClient.PullAllEnrolledAsync(
                ep, delay, pushWait, cancellationToken, enrichPhotos: false);
        }
        catch (OperationCanceledException)
        {
            errors.Add($"{dev.DeviceName}: pull timed out (12 min cap). Try one device; enable push if list is incomplete.");
            return (hubRows, errors, 0, 0);
        }

        var batch = pull.Employees.ToList();
        if (batch.Count == 0 || !pull.IsComplete)
            batch = await SupplementFromPortalAsync(
                dev, ep, batch, pull, delay, cancellationToken, maxSupplementIds);

        if (batch.Count == 0)
            errors.Add($"{dev.DeviceName} ({dev.DeviceIp}): {pull.Error ?? "no users"}");
        else if (!pull.IsComplete && pull.DeviceEnrolledCount > batch.Count)
            errors.Add($"{dev.DeviceName}: reports {pull.DeviceEnrolledCount}, pulled {batch.Count} (incomplete).");

        foreach (var e in batch)
            hubRows.Add(ToHubRow(dev, e));

        return (hubRows, errors, pull.DeviceEnrolledCount, pull.ElapsedMs);
    }

    private async Task<List<DeviceEmployeeRecord>> SupplementFromPortalAsync(
        BiometricDevice dev,
        DeviceEndpoint ep,
        List<DeviceEmployeeRecord> batch,
        DeviceEmployeePullResult pull,
        int delay,
        CancellationToken cancellationToken,
        int maxSupplementIds = 10_000)
    {
        var have = batch
            .Select(e => NicIdNormalizer.Normalize(e.EmployeeId))
            .Where(x => !string.IsNullOrEmpty(x))
            .ToHashSet(StringComparer.Ordinal);

        var portalIds = await attendance.GetDistinctEmployeeIdsAsync(dev.DeviceId, cancellationToken: cancellationToken);
        var registryIds = await employeeRegistry.ListEmployeeIdsAsync(dev.DeviceId, cancellationToken);
        var supplementalIds = portalIds
            .Concat(registryIds)
            .Select(NicIdNormalizer.Normalize)
            .Where(x => !string.IsNullOrEmpty(x) && !have.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Clamp(maxSupplementIds, 0, 10_000))
            .ToList();

        if (supplementalIds.Count == 0 || (batch.Count > 0 && pull.IsComplete))
            return batch;

        var viaIds = await deviceClient.PullEmployeesAsync(ep, supplementalIds, delay, cancellationToken);
        foreach (var e in viaIds.Employees)
        {
            var nid = NicIdNormalizer.Normalize(e.EmployeeId);
            if (string.IsNullOrEmpty(nid) || have.Contains(nid)) continue;
            have.Add(nid);
            e.BioDeviceId = dev.DeviceId;
            batch.Add(e);
        }

        return batch;
    }

    private async Task<IReadOnlyList<BiometricDevice>> ResolveTargetDevicesAsync(
        IReadOnlyList<long>? deviceIds,
        bool allDevices,
        CancellationToken cancellationToken)
    {
        if (allDevices)
            return await devices.ListDevicesAsync(activeOnly: true, cancellationToken);

        var ids = deviceIds?.Where(x => x > 0).Distinct().ToList() ?? [];
        if (ids.Count == 0) return [];

        var all = await devices.ListDevicesAsync(activeOnly: false, cancellationToken);
        return all.Where(d => ids.Contains(d.DeviceId)).ToList();
    }

    private DeviceEndpoint ToEndpoint(BiometricDevice d) => new()
    {
        DeviceId = d.DeviceId,
        Host = d.DeviceIp,
        Port = d.DevicePort,
        RecordStart = _opts.RecordStart,
        RecordEnd = _opts.RecordEnd,
    };

    private static object ToHubRow(BiometricDevice dev, DeviceEmployeeRecord e)
    {
        var bio = EmployeeBiometricStatus.FromRecord(e);
        return new
        {
            deviceId = dev.DeviceId,
            deviceName = dev.DeviceName,
            deviceIp = dev.DeviceIp,
            employeeId = e.EmployeeId,
            name = e.Name,
            department = e.Department,
            cardNumber = e.CardNumber,
            authority = e.Authority,
            checkType = e.CheckType,
            faceTemplateCount = e.FaceTemplateCount,
            biometricStatus = bio.ToString(),
            biometricLabel = EmployeeBiometricStatus.ToLabel(bio),
            enrolledAt = e.EnrolledAtUtc is null
                ? null
                : (e.EnrolledAtUtc.Value.Kind == DateTimeKind.Utc
                    ? e.EnrolledAtUtc.Value.ToLocalTime()
                    : e.EnrolledAtUtc.Value).ToString("yyyy-MM-dd HH:mm:ss"),
            enrolledAtSource = e.EnrolledAtSource,
        };
    }

    private static DateTime? ParseEnrolled(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var raw = text.Split('·')[0].Trim();
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AssumeLocal, out var dt) ? dt : null;
    }

    private static List<long> ParseIdList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    public sealed class HubFetchRequest
    {
        public List<long>? DeviceIds { get; set; }
        public bool AllDevices { get; set; }
        public int? DelayMs { get; set; }
        public int? PushWaitSeconds { get; set; }
    }

    public sealed class HubFetchOneRequest
    {
        public long DeviceId { get; set; }
        public int? DelayMs { get; set; }
        public int? PushWaitSeconds { get; set; }
    }

    public sealed class HubCompareRequest
    {
        public List<HubRowDto>? Rows { get; set; }
    }

    public sealed class HubSaveRequest
    {
        public List<HubRowDto>? Rows { get; set; }
        public bool AlsoUpdateRegistry { get; set; }
        public string? Note { get; set; }
    }

    public sealed class HubRowDto
    {
        public long DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? EmployeeId { get; set; }
        public string? Name { get; set; }
        public string? Department { get; set; }
        public string? CardNumber { get; set; }
        public string? Authority { get; set; }
        public string? CheckType { get; set; }
        public int? FaceTemplateCount { get; set; }
        public string? EnrolledAt { get; set; }
        public string? EnrolledAtSource { get; set; }
    }
}