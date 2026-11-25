using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Core.Utilities;
using BiometricDashboard.Web.Authorization;
using BiometricDashboard.Web.Models;
using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace BiometricDashboard.Web.Controllers;

[Route("Device/EmployeePhotos")]
[RequirePermission("device_photos.view")]
public sealed class DeviceEmployeePhotosController(
    IDeviceRepository devices,
    IDeviceClient deviceClient,
    IEmployeeRegistryRepository employeeRegistry,
    IEmployeePhotoCache photoCache,
    EmployeePhotoBulkZipService bulkZip,
    SelectedDeviceService selection,
    IOptions<DeviceOptions> deviceOptions) : Controller
{
    private readonly DeviceOptions _opts = deviceOptions.Value;

    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, CancellationToken cancellationToken)
    {
        var selected = await selection.GetSelectedDeviceAsync(cancellationToken);
        var all = await devices.ListDevicesAsync(activeOnly: true, cancellationToken: cancellationToken);

        var snap = selected is not null
            ? await deviceClient.GetDeviceSnapshotAsync(ToEndpoint(selected), cancellationToken)
            : null;

        ViewBag.InitialQuery = q?.Trim();
        ViewBag.DeviceLabel = selected is not null
            ? $"{selected.DeviceName} ({selected.DeviceIp})"
            : "No device selected";
        ViewBag.DeviceId = selected?.DeviceId;
        ViewBag.DevicesJson = JsonSerializer.Serialize(
            all.Select(d => new { d.DeviceId, d.DeviceName, d.DeviceIp, d.IsActive }),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        ViewBag.PageBand = new PortalPageBandViewModel
        {
            Eyebrow = "HDCP · GetEmployee",
            Title = "Employee photo gallery",
            IconClass = "bi-person-bounding-box",
            SubtitleEmElementId = "depHeroDevice",
            SubtitleEmText = ViewBag.DeviceLabel as string,
            SubtitleMuted = "Pull 大头照 (photo_data) from the terminal · search by NIC or registry name",
            Stats =
            [
                new()
                {
                    Label = "Terminal",
                    Value = all.Count > 0 ? (selected?.DeviceName ?? "—") : "—",
                    Meta = snap?.IsOnline == true ? "online" : snap?.LastError ?? "pick below",
                    IconClass = "bi-hdd-network",
                    ColorIndex = 1,
                    ValueElementId = "depStatDev",
                },
                new()
                {
                    Label = "On device",
                    Value = snap is { IsOnline: true } ? snap.EmployeeCount.ToString() : "—",
                    Meta = "enrolled users",
                    IconClass = "bi-people",
                    ColorIndex = 2,
                    ValueElementId = "depStatUsers",
                },
                new()
                {
                    Label = "Face enrolled",
                    Value = snap is { IsOnline: true } ? snap.FaceEnrolled.ToString() : "—",
                    Meta = "GetDeviceInfo",
                    IconClass = "bi-camera",
                    ColorIndex = 3,
                    ValueElementId = "depStatFace",
                },
            ],
        };

        return View();
    }

    [HttpGet("Lookup")]
    public async Task<IActionResult> Lookup(long? deviceId, string q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Json(new { ok = false, error = "Enter employee ID or name." });

        var dev = await ResolveTargetDeviceAsync(deviceId, cancellationToken);
        if (dev is null)
            return Json(new { ok = false, error = "Select an active device." });

        var lookupId = NicIdNormalizer.Normalize(q);
        if (string.IsNullOrEmpty(lookupId))
        {
            var reg = await employeeRegistry.ListActiveAsync(dev.DeviceId, cancellationToken);
            var needle = q.Trim();
            var nameMatches = reg
                .Where(e =>
                    (e.DisplayName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                    || e.EmployeeId.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .Select(e => new { e.EmployeeId, displayName = e.DisplayName })
                .ToList();

            if (nameMatches.Count == 0)
                return Json(new { ok = false, error = "No registry match — search by NIC/employee ID." });
            if (nameMatches.Count > 1)
                return Json(new { ok = true, ambiguous = true, matches = nameMatches });

            lookupId = nameMatches[0].EmployeeId;
        }

        var lookup = await deviceClient.LookupEmployeeWithPhotoAsync(ToEndpoint(dev), lookupId, cancellationToken);
        if (!lookup.Found || lookup.Employee is null)
            return Json(new { ok = false, error = lookup.Error ?? "Not found on device." });

        lookup.Employee.DeviceName = dev.DeviceName;
        lookup.Employee.DeviceIp = dev.DeviceIp;
        lookup.Employee.BioDeviceId = dev.DeviceId;

        string? photoUrl = null;
        if (lookup.HasDisplayPhoto)
        {
            photoUrl = Url.Action(
                nameof(EmployeePhoto),
                new { deviceId = dev.DeviceId, employeeId = lookup.Employee.EmployeeId });
        }

        return Json(new
        {
            ok = true,
            employee = ToEmployeeJson(lookup.Employee),
            hasPhoto = lookup.HasDisplayPhoto,
            photoHint = lookup.PhotoHint,
            photoUrl,
            hdcp = lookup.HdcpMethod,
        });
    }

    [HttpGet("Photo")]
    [ResponseCache(NoStore = true, Duration = 0)]
    public async Task<IActionResult> EmployeePhoto(long deviceId, string employeeId, CancellationToken cancellationToken)
    {
        var id = NicIdNormalizer.Normalize(employeeId);
        if (string.IsNullOrEmpty(id) || deviceId <= 0)
            return NotFound();

        if (photoCache.TryGet(deviceId, id, out var bytes, out var contentType) && bytes is { Length: > 0 })
        {
            var ct = contentType ?? "image/jpeg";
            if (Request.Query.ContainsKey("download"))
            {
                var fileName = EmployeePhotoFileNames.Build(Request.Query["name"].FirstOrDefault(), id, ct);
                return File(bytes, ct, fileName);
            }
            return File(bytes, ct);
        }

        var dev = await devices.GetByIdAsync(deviceId, cancellationToken);
        if (dev is null || !dev.IsActive)
            return NotFound();

        var lookup = await deviceClient.LookupEmployeeWithPhotoAsync(ToEndpoint(dev), id, cancellationToken);
        if (!lookup.HasDisplayPhoto)
            return NotFound();

        if (photoCache.TryGet(deviceId, id, out bytes, out contentType) && bytes is { Length: > 0 })
        {
            var ct = contentType ?? "image/jpeg";
            if (Request.Query.ContainsKey("download"))
            {
                var fileName = EmployeePhotoFileNames.Build(Request.Query["name"].FirstOrDefault(), id, ct);
                return File(bytes, ct, fileName);
            }
            return File(bytes, ct);
        }

        return NotFound();
    }

    [HttpPost("BulkDownload")]
    public async Task<IActionResult> BulkDownload([FromBody] EmployeePhotoBulkDownloadRequest body, CancellationToken cancellationToken)
    {
        if (body.Items is null || body.Items.Count == 0)
            return BadRequest(new { ok = false, error = "No employees selected for download." });

        var items = body.Items
            .Where(i => i.DeviceId > 0 && !string.IsNullOrWhiteSpace(i.EmployeeId))
            .Select(i => new EmployeePhotoBulkZipService.Item
            {
                DeviceId = i.DeviceId,
                EmployeeId = i.EmployeeId,
                Name = i.Name,
            })
            .ToList();

        var zip = await bulkZip.BuildZipAsync(items, cancellationToken);
        if (!zip.Succeeded || zip.ZipBytes is null)
            return NotFound(new { ok = false, error = zip.Error });

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return File(zip.ZipBytes, "application/zip", $"employee-photos-{stamp}.zip");
    }

    private async Task<BiometricDevice?> ResolveTargetDeviceAsync(long? deviceId, CancellationToken cancellationToken)
    {
        if (deviceId is > 0)
            return await devices.GetByIdAsync(deviceId.Value, cancellationToken);
        return await selection.GetSelectedDeviceAsync(cancellationToken);
    }

    private DeviceEndpoint ToEndpoint(BiometricDevice d) => new()
    {
        DeviceId = d.DeviceId,
        Host = d.DeviceIp,
        Port = d.DevicePort,
        RecordStart = _opts.RecordStart,
        RecordEnd = _opts.RecordEnd,
    };

    private static object ToEmployeeJson(DeviceEmployeeRecord e)
    {
        var bio = EmployeeBiometricStatus.FromRecord(e);
        return new
        {
            e.EmployeeId,
            e.Name,
            e.CardNumber,
            e.Department,
            e.Authority,
            e.FaceTemplateCount,
            checkType = e.CheckType,
            biometricStatus = bio.ToString(),
            biometricLabel = EmployeeBiometricStatus.ToLabel(bio),
            e.DeviceName,
            e.DeviceIp,
            e.BioDeviceId,
        };
    }
}