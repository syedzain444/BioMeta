using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using BiometricDashboard.Web.Authorization;
using BiometricDashboard.Web.Models;
using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace BiometricDashboard.Web.Controllers;

[Route("Device/EnrolledUsers")]
[RequirePermission("devices.view")]
public sealed class DeviceEnrolledUsersController(
    IDeviceRepository devices,
    IDeviceClient deviceClient,
    IAttendanceRepository attendance,
    IEmployeeRegistryRepository employeeRegistry,
    IEmployeeSyncJobRepository syncJobHistory,
    IEmployeePhotoCache photoCache,
    EmployeePhotoBulkZipService bulkZip,
    SelectedDeviceService selection,
    IEmployeeSyncJobService syncJobs,
    ICurrentUserContext currentUser,
    IServiceScopeFactory scopeFactory,
    IOptions<DeviceOptions> deviceOptions,
    ILogger<DeviceEnrolledUsersController> logger) : Controller
{
    private readonly DeviceOptions _opts = deviceOptions.Value;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var selected = await selection.GetSelectedDeviceAsync(cancellationToken);
        var all = await devices.ListDevicesAsync(activeOnly: true, cancellationToken: cancellationToken);

        var snap = selected is not null
            ? await deviceClient.GetDeviceSnapshotAsync(ToEndpoint(selected), cancellationToken)
            : null;

        ViewBag.DeviceLabel = selected is not null
            ? $"{selected.DeviceName} ({selected.DeviceIp})"
            : "No device selected";
        ViewBag.DeviceId = selected?.DeviceId;
        ViewBag.DevicesJson = JsonSerializer.Serialize(
            all.Select(d => new
            {
                d.DeviceId,
                d.DeviceName,
                d.DeviceIp,
                d.IsActive,
            }),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        ViewBag.EnrolledDeviceCount = all.Count;

        ViewBag.PageBand = new PortalPageBandViewModel
        {
            Eyebrow = "HDCP · Port 9922",
            Title = "Device enrolled users",
            IconClass = "bi-person-vcard",
            SubtitleEmElementId = "geuHeroDevice",
            SubtitleEmText = ViewBag.DeviceLabel as string,
            SubtitleMuted = "Employee ID = NIC without dashes · one TCP session per batch",
            Stats =
            [
                new()
                {
                    Label = "On device",
                    Value = snap is { IsOnline: true } ? snap.EmployeeCount.ToString() : "—",
                    Meta = snap?.IsOnline == true ? "enrolled count" : snap?.LastError ?? "offline",
                    IconClass = "bi-list-ul",
                    ColorIndex = 1,
                    ValueElementId = "stOn",
                },
                new()
                {
                    Label = "Face enrolled",
                    Value = snap is { IsOnline: true } ? snap.FaceEnrolled.ToString() : "—",
                    Meta = "from GetDeviceInfo",
                    IconClass = "bi-person-bounding-box",
                    ColorIndex = 2,
                    ValueElementId = "stFace",
                },
                new()
                {
                    Label = "Devices",
                    Value = all.Count.ToString(),
                    Meta = "active in portal",
                    IconClass = "bi-diagram-3",
                    ColorIndex = 3,
                    ValueElementId = "stDev",
                },
            ],
        };

        return View();
    }

    [HttpGet("Ids")]
    public async Task<IActionResult> Ids(string source, long? deviceId, CancellationToken cancellationToken)
    {
        source = (source ?? "device").Trim().ToLowerInvariant();
        var selected = await ResolveTargetDeviceAsync(deviceId, cancellationToken);
        if (selected is null)
            return Json(new { ok = false, error = "Select a device." });

        IReadOnlyList<string> ids;
        switch (source)
        {
            case "portal":
                ids = await attendance.GetDistinctEmployeeIdsAsync(selected.DeviceId, cancellationToken: cancellationToken);
                ids = ids.Select(NicIdNormalizer.Normalize).Distinct().ToList();
                break;
            case "device":
                ids = await deviceClient.GetEmployeeIdsFromDeviceAttendanceAsync(ToEndpoint(selected), cancellationToken);
                break;
            default:
                return Json(new { ok = false, error = "Unknown source." });
        }

        return Json(new
        {
            ok = true,
            source,
            deviceId = selected.DeviceId,
            deviceName = selected.DeviceName,
            count = ids.Count,
            ids,
        });
    }

    [HttpPost("Photos/BulkDownload")]
    public async Task<IActionResult> BulkDownloadPhotos(
        [FromBody] EmployeePhotoBulkDownloadRequest body,
        CancellationToken cancellationToken)
    {
        if (body.Items is null || body.Items.Count == 0)
            return BadRequest(new { ok = false, error = "No employees in the list." });

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
            return NotFound(new { ok = false, error = zip.Error, added = zip.Added });

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return File(zip.ZipBytes, "application/zip", $"employee-photos-{stamp}.zip");
    }

    [HttpPost("PullBatch")]
    public async Task<IActionResult> PullBatch([FromBody] PullBatchRequest body, CancellationToken cancellationToken)
    {
        if (body.Ids is null || body.Ids.Count == 0)
            return Json(new { ok = false, error = "No employee IDs in batch." });

        var delay = body.DelayMs is > 0 and < 2000 ? body.DelayMs.Value : 120;
        var targetDevices = await ResolveDevicesAsync(body.DeviceId, body.AllDevices, cancellationToken);
        if (targetDevices.Count == 0)
            return Json(new { ok = false, error = "No device available." });

        var employees = new List<DeviceEmployeeRecord>();
        long elapsed = 0;
        var enrolled = 0;
        string? error = null;

        foreach (var dev in targetDevices)
        {
            var ep = ToEndpoint(dev);
            var pull = await deviceClient.PullEmployeesAsync(ep, body.Ids, delay, cancellationToken);
            elapsed += pull.ElapsedMs;
            if (pull.DeviceEnrolledCount > enrolled)
                enrolled = pull.DeviceEnrolledCount;
            if (!pull.Succeeded && error is null)
                error = pull.Error;

            foreach (var e in pull.Employees)
            {
                e.DeviceName = dev.DeviceName;
                e.DeviceIp = dev.DeviceIp;
                e.BioDeviceId = dev.DeviceId;
                employees.Add(e);
            }
        }

        await EnrichEnrollmentDatesAsync(employees, cancellationToken);
        await EnrichPhotosForPulledEmployeesAsync(targetDevices, employees, delay, cancellationToken);

        return Json(new
        {
            ok = string.IsNullOrEmpty(error) || employees.Count > 0,
            error,
            requested = body.Ids.Count,
            found = employees.Count,
            deviceEnrolledCount = enrolled,
            elapsedMs = elapsed,
            employees = employees.Select(ToEmployeeRow),
        });
    }

    [HttpPost("PullAll")]
    [RequestTimeout(milliseconds: 1_800_000)]
    public async Task<IActionResult> PullAll([FromBody] PullAllRequest body, CancellationToken cancellationToken)
    {
        var delay = body.DelayMs is > 0 and < 2000 ? body.DelayMs.Value : 120;
        var pushWait = body.PushWaitSeconds is > 0 and <= 300 ? body.PushWaitSeconds.Value : 240;
        var targetDevices = await ResolveDevicesAsync(body.DeviceId, body.AllDevices, cancellationToken);
        if (targetDevices.Count == 0)
            return Json(new { ok = false, error = "No device available." });

        var employees = new List<DeviceEmployeeRecord>();
        long elapsed = 0;
        var enrolled = 0;
        string? method = null;
        string? error = null;
        string? warning = null;
        var incompleteDevices = 0;

        foreach (var dev in targetDevices)
        {
            var ep = ToEndpoint(dev);
            var pull = await deviceClient.PullAllEnrolledAsync(ep, delay, pushWait, cancellationToken);
            elapsed += pull.ElapsedMs;
            if (pull.DeviceEnrolledCount > enrolled)
                enrolled = pull.DeviceEnrolledCount;
            if (!string.IsNullOrEmpty(pull.Method))
                method = pull.Method;
            if (!string.IsNullOrWhiteSpace(pull.Warning))
                warning = pull.Warning;

            var batch = pull.Employees.ToList();
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
                .Take(10_000)
                .ToList();

            if (supplementalIds.Count > 0
                && (batch.Count == 0 || !pull.IsComplete || batch.Count < pull.DeviceEnrolledCount))
            {
                var viaIds = await deviceClient.PullEmployeesAsync(ep, supplementalIds, delay, cancellationToken);
                elapsed += viaIds.ElapsedMs;
                foreach (var e in viaIds.Employees)
                {
                    var nid = NicIdNormalizer.Normalize(e.EmployeeId);
                    if (string.IsNullOrEmpty(nid) || have.Contains(nid)) continue;
                    have.Add(nid);
                    batch.Add(e);
                }

                if (viaIds.Employees.Count > 0 && string.IsNullOrEmpty(method))
                    method = "supplement-portal-registry";
            }

            var deviceComplete = pull.DeviceEnrolledCount <= 0 || batch.Count >= pull.DeviceEnrolledCount;
            if (!deviceComplete)
            {
                incompleteDevices++;
                var miss = pull.DeviceEnrolledCount - batch.Count;
                var part =
                    $"{dev.DeviceName} ({dev.DeviceIp}): device reports {pull.DeviceEnrolledCount}, pulled {batch.Count} ({miss} missing).";
                warning = string.IsNullOrEmpty(warning) ? part : warning + " " + part;
            }

            if (batch.Count == 0)
            {
                var detail = pull.Error ?? "No users read from device.";
                error = targetDevices.Count == 1
                    ? $"{dev.DeviceName} ({dev.DeviceIp}): {detail}"
                    : string.IsNullOrEmpty(error)
                        ? $"{dev.DeviceName} ({dev.DeviceIp}): {detail}"
                        : error + " · " + $"{dev.DeviceName}: no users";
            }
            else if (targetDevices.Count > 1 && deviceComplete)
            {
                error = null;
            }

            foreach (var e in batch)
            {
                e.DeviceName = dev.DeviceName;
                e.DeviceIp = dev.DeviceIp;
                e.BioDeviceId = dev.DeviceId;
                employees.Add(e);
            }
        }

        await EnrichEnrollmentDatesAsync(employees, cancellationToken);
        await EnrichPhotosForPulledEmployeesAsync(targetDevices, employees, delay, cancellationToken);

        return Json(new
        {
            ok = employees.Count > 0,
            error = employees.Count > 0 ? null : error,
            warning,
            incomplete = incompleteDevices > 0,
            method,
            found = employees.Count,
            deviceEnrolledCount = enrolled,
            elapsedMs = elapsed,
            employees = employees.Select(ToEmployeeRow),
        });
    }

    [HttpPost("PullRecentEnrolled")]
    [RequestTimeout(milliseconds: 900_000)]
    public async Task<IActionResult> PullRecentEnrolled(
        [FromBody] PullRecentEnrolledRequest? body,
        CancellationToken cancellationToken)
    {
        body ??= new PullRecentEnrolledRequest();
        try
        {
            var lookbackDays = body.LookbackDays is > 0 and <= 31 ? body.LookbackDays.Value : 3;
            var delay = body.DelayMs is > 0 and < 2000 ? body.DelayMs.Value : 120;
            var targetDevices = await ResolveDevicesAsync(body.DeviceId, body.AllDevices, cancellationToken);
            if (targetDevices.Count == 0)
                return Json(new { ok = false, error = "No active device available." });

            var (startLocal, endLocal) = ResolveRecentEnrollWindow(lookbackDays);
            var endExclusive = DateTime.Now.Date.AddDays(1);
            var employees = new List<DeviceEmployeeRecord>();
            long elapsed = 0;
            var portalCandidateTotal = 0;
            string? error = null;
            var devicesScanned = 0;

            foreach (var dev in targetDevices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var deviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deviceCts.CancelAfter(TimeSpan.FromSeconds(120));
                var deviceToken = deviceCts.Token;

                try
                {
                    var ep = ToEndpoint(dev);
                    IReadOnlyList<string> portalIds = [];
                    try
                    {
                        portalIds = await attendance.GetEmployeeIdsWithFirstPunchBetweenAsync(
                            startLocal,
                            endExclusive,
                            dev.DeviceId,
                            cancellationToken: deviceToken);
                        portalCandidateTotal += portalIds.Count;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Recent enroll: portal ID query failed for device {DeviceId}", dev.DeviceId);
                    }

                    var recordIds = await deviceClient.GetEmployeeIdsFromDeviceRecordsInRangeAsync(
                        ep, startLocal, endLocal, deviceToken);

                    var candidateIds = recordIds
                        .Concat(portalIds)
                        .Select(NicIdNormalizer.Normalize)
                        .Where(x => !string.IsNullOrEmpty(x))
                        .Distinct(StringComparer.Ordinal)
                        .Take(2500)
                        .ToList();

                    devicesScanned++;
                    if (candidateIds.Count == 0)
                        continue;

                    var pull = await deviceClient.PullEmployeesAsync(ep, candidateIds, delay, deviceToken);
                    elapsed += pull.ElapsedMs;
                    if (!pull.Succeeded && error is null)
                        error = $"{dev.DeviceName}: {pull.Error}";

                    foreach (var e in pull.Employees)
                    {
                        if (!EmployeeEnrolledInWindow(e, startLocal, endLocal))
                            continue;
                        e.DeviceName = dev.DeviceName;
                        e.DeviceIp = dev.DeviceIp;
                        e.BioDeviceId = dev.DeviceId;
                        employees.Add(e);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    error ??= $"{dev.DeviceName}: timed out (skipped).";
                    logger.LogWarning("Recent enroll: device {Device} timed out after 120s", dev.DeviceName);
                }
                catch (Exception ex)
                {
                    error ??= $"{dev.DeviceName}: {ex.Message}";
                    logger.LogWarning(ex, "Recent enroll: device {Device} failed", dev.DeviceName);
                }
            }

            await EnrichEnrollmentDatesAsync(employees, cancellationToken);
            employees = employees.Where(e => EmployeeEnrolledInWindow(e, startLocal, endLocal)).ToList();

            if (employees.Count > 0)
            {
                try
                {
                    await EnrichPhotosForPulledEmployeesAsync(targetDevices, employees, delay, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Recent enroll: photo enrich skipped");
                }
            }

            var windowLabel =
                $"{startLocal:yyyy-MM-dd} — {endLocal:yyyy-MM-dd} (local, {lookbackDays} day(s))";

            return Json(new
            {
                ok = employees.Count > 0,
                error = employees.Count > 0 ? null : error ?? "No users enrolled in this date window on the selected device(s).",
                method = "recent-enroll",
                found = employees.Count,
                lookbackDays,
                windowStart = startLocal.ToString("yyyy-MM-dd"),
                windowEnd = endLocal.ToString("yyyy-MM-dd"),
                windowLabel,
                deviceCount = devicesScanned,
                portalCandidateCount = portalCandidateTotal,
                elapsedMs = elapsed,
                employees = employees.Select(ToEmployeeRow),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PullRecentEnrolled failed");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpPost("Import/Validate")]
    public async Task<IActionResult> ValidateImport([FromBody] ImportValidateRequest body, CancellationToken cancellationToken)
    {
        var rows = (body.Rows ?? []).Select((r, i) => new EmployeeImportRow
        {
            Line = r.Line > 0 ? r.Line : i + 1,
            EmployeeId = r.EmployeeId ?? "",
            Name = r.Name,
            Department = r.Department,
            CardNumber = r.CardNumber,
            Authority = r.Authority,
            FaceDataBase64 = r.FaceDataBase64,
        }).ToList();

        if (rows.Count == 0 && !string.IsNullOrWhiteSpace(body.Text))
        {
            var parsed = (body.Text ?? "")
                .Split(['\r', '\n', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select((part, i) => new EmployeeImportRow
                {
                    Line = i + 1,
                    EmployeeId = part.Trim(),
                });
            rows.AddRange(parsed);
        }

        var result = EmployeeImportValidator.Validate(rows);
        if (body.SaveToRegistry == true)
        {
            var dev = await ResolveTargetDeviceAsync(body.DeviceId, cancellationToken);
            await employeeRegistry.UpsertManyAsync(
                result.Rows.Where(r => r.IsValid).ToList(),
                dev?.DeviceId,
                currentUser.UserId,
                "validate-import",
                cancellationToken);
        }

        return Json(new
        {
            ok = true,
            totalRows = result.TotalRows,
            validRows = result.ValidRows,
            invalidRows = result.InvalidRows,
            rows = result.Rows.Select(r => new
            {
                r.Line,
                r.EmployeeId,
                r.Name,
                r.Department,
                r.CardNumber,
                r.Authority,
                hasFace = !string.IsNullOrWhiteSpace(r.FaceDataBase64),
                r.Errors,
                r.Warnings,
                valid = r.IsValid,
            }),
        });
    }

    [HttpPost("Compare")]
    public async Task<IActionResult> Compare([FromBody] CompareRequest body, CancellationToken cancellationToken)
    {
        var selected = await ResolveTargetDeviceAsync(body.DeviceId, cancellationToken);
        if (selected is null)
            return Json(new { ok = false, error = "Select a device." });

        var portalIds = new List<string>();
        var source = (body.PortalSource ?? "db").Trim().ToLowerInvariant();
        if (source == "import" && body.PortalIds is { Count: > 0 })
        {
            portalIds.AddRange(body.PortalIds.Select(NicIdNormalizer.Normalize).Where(x => !string.IsNullOrEmpty(x)));
        }
        else if (source == "registry")
        {
            portalIds.AddRange(await employeeRegistry.ListEmployeeIdsAsync(selected.DeviceId, cancellationToken));
        }
        else
        {
            var dbIds = await attendance.GetDistinctEmployeeIdsAsync(selected.DeviceId, cancellationToken: cancellationToken);
            portalIds.AddRange(dbIds.Select(NicIdNormalizer.Normalize));
        }

        portalIds = portalIds.Distinct(StringComparer.Ordinal).ToList();
        var ep = ToEndpoint(selected);
        ep.DeviceId = selected.DeviceId;
        var diff = await deviceClient.ComparePortalAndDeviceAsync(ep, portalIds, body.DelayMs ?? 120, cancellationToken);
        diff.DeviceName = selected.DeviceName;

        return Json(new
        {
            ok = diff.Succeeded,
            error = diff.Error,
            deviceId = diff.DeviceId,
            deviceName = diff.DeviceName,
            portalCount = diff.PortalCount,
            deviceCount = diff.DeviceCount,
            matchedCount = diff.MatchedCount,
            elapsedMs = diff.ElapsedMs,
            onlyOnPortal = diff.OnlyOnPortal.Select(ToDiffJson),
            onlyOnDevice = diff.OnlyOnDevice.Select(ToDiffJson),
            onBoth = diff.OnBoth.Select(ToDiffJson),
        });
    }

    [HttpPost("Sync/Start")]
    [RequirePermission("devices.manage")]
    public async Task<IActionResult> SyncStart([FromBody] SyncStartRequest body, CancellationToken cancellationToken)
    {
        var selected = await ResolveTargetDeviceAsync(body.DeviceId, cancellationToken);
        if (selected is null)
            return Json(new { ok = false, error = "Select a device." });

        var rows = (body.Rows ?? [])
            .Select(r => new EmployeeImportRow
            {
                Line = r.Line,
                EmployeeId = r.EmployeeId ?? "",
                Name = r.Name,
                Department = r.Department,
                CardNumber = r.CardNumber,
                Authority = r.Authority,
                FaceDataBase64 = r.FaceDataBase64,
            })
            .ToList();
        var validation = EmployeeImportValidator.Validate(rows);
        var valid = validation.Rows.Where(r => r.IsValid).ToList();
        if (valid.Count == 0)
            return Json(new { ok = false, error = "No valid rows to upload." });

        if (body.SaveToRegistry != false)
        {
            await employeeRegistry.UpsertManyAsync(valid, selected.DeviceId, currentUser.UserId, "sync-upload", cancellationToken);
        }

        var job = syncJobs.CreateJob(selected.DeviceId, selected.DeviceIp, selected.DeviceName, valid, "upload");
        job.State = EmployeeSyncJobState.Queued;
        job.LogLine = "Queued " + valid.Count + " employee(s).";
        await syncJobs.SaveJobAsync(job, currentUser.UserId, isNew: true, cancellationToken);

        var employees = valid.Select(ImportToDeviceRecord).ToList();
        var ep = ToEndpoint(selected);
        ep.DeviceId = selected.DeviceId;
        var jobId = job.JobId;
        var delay = body.DelayMs is > 0 and < 2000 ? body.DelayMs.Value : 150;
        var pushWait = body.PushWaitSeconds is > 0 and <= 300 ? body.PushWaitSeconds.Value : 120;

        _ = RunUploadJobAsync(jobId, ep, employees, delay, pushWait);

        return Json(new { ok = true, jobId, total = valid.Count });
    }

    [HttpGet("Sync/Status/{jobId:guid}")]
    public async Task<IActionResult> SyncStatus(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await syncJobs.GetJobAsync(jobId, cancellationToken);
        if (job is null)
            return Json(new { ok = false, error = "Job not found." });
        return Json(new { ok = true, job = ToJobJson(job) });
    }

    [HttpGet("Registry")]
    public async Task<IActionResult> RegistryList(long? deviceId, CancellationToken cancellationToken)
    {
        var devId = deviceId;
        if (devId is null or <= 0)
        {
            var selected = await ResolveTargetDeviceAsync(null, cancellationToken);
            devId = selected?.DeviceId;
        }

        var rows = await employeeRegistry.ListActiveAsync(devId, cancellationToken);
        return Json(new
        {
            ok = true,
            count = rows.Count,
            deviceId = devId,
            rows = rows.Select(r => new
            {
                r.EmployeeId,
                name = r.DisplayName,
                r.Department,
                r.CardNumber,
                r.Authority,
                r.BiometricStatus,
                r.HasFaceData,
                updatedAt = r.UpdatedAt,
            }),
        });
    }

    [HttpGet("Sync/History")]
    public async Task<IActionResult> SyncHistory(long? deviceId, int? limit, CancellationToken cancellationToken)
    {
        var dev = await ResolveTargetDeviceAsync(deviceId, cancellationToken);
        var jobs = await syncJobHistory.ListRecentAsync(dev?.DeviceId, limit ?? 25, cancellationToken);
        return Json(new { ok = true, jobs = jobs.Select(ToJobJson) });
    }

    [HttpPost("Device/DeleteExtra")]
    [RequirePermission("devices.manage")]
    public async Task<IActionResult> DeleteExtraOnDevice([FromBody] DeleteExtraRequest body, CancellationToken cancellationToken)
    {
        var selected = await ResolveTargetDeviceAsync(body.DeviceId, cancellationToken);
        if (selected is null)
            return Json(new { ok = false, error = "Select a device." });
        var ids = (body.EmployeeIds ?? [])
            .Select(NicIdNormalizer.Normalize)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
            return Json(new { ok = false, error = "No employee IDs." });

        var job = syncJobs.CreateJob(selected.DeviceId, selected.DeviceIp, selected.DeviceName, [], "delete");
        job.Total = ids.Count;
        job.State = EmployeeSyncJobState.Deleting;
        job.LogLine = "Deleting " + ids.Count + " extra user(s) on device…";
        await syncJobs.SaveJobAsync(job, currentUser.UserId, isNew: true, cancellationToken);

        var ep = ToEndpoint(selected);
        ep.DeviceId = selected.DeviceId;
        var delay = body.DelayMs is > 0 and < 2000 ? body.DelayMs.Value : 120;
        _ = RunDeleteJobAsync(job.JobId, ep, ids, delay);

        return Json(new { ok = true, jobId = job.JobId, total = ids.Count });
    }

    [HttpPost("Sync/Cancel")]
    [RequirePermission("devices.manage")]
    public IActionResult SyncCancel([FromBody] SyncCancelRequest body)
    {
        if (body.JobId == Guid.Empty)
            return Json(new { ok = false, error = "Missing job id." });
        syncJobs.CancelJob(body.JobId);
        var job = syncJobs.GetJob(body.JobId);
        if (job is not null)
            _ = syncJobs.SaveJobAsync(job, currentUser.UserId, isNew: false, CancellationToken.None);
        return Json(new { ok = true });
    }

    private async Task RunUploadJobAsync(
        Guid jobId,
        DeviceEndpoint ep,
        List<DeviceEmployeeRecord> employees,
        int delayMs,
        int pushWaitSeconds)
    {
        var job = syncJobs.GetJob(jobId);
        if (job is null) return;
        try
        {
            job.State = EmployeeSyncJobState.Uploading;
            job.LogLine = "Uploading to " + job.DeviceName + "…";
            await syncJobs.SaveJobAsync(job, null, isNew: false, CancellationToken.None);

            using var scope = scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IDeviceClient>();
            var upload = await client.UploadEmployeesAsync(ep, jobId, employees, delayMs, pushWaitSeconds, CancellationToken.None);

            job.Uploaded = upload.Uploaded;
            job.Failed = upload.Failed;
            job.Total = upload.Requested;
            var lineResults = upload.Lines.ToList();
            job.LineResults = lineResults;
            job.State = EmployeeSyncJobState.Verifying;
            job.LogLine = "Verifying with GetEmployee…";
            await syncJobs.SaveJobAsync(job, null, isNew: false, CancellationToken.None);

            await client.VerifyEmployeesOnDeviceAsync(ep, lineResults, delayMs, CancellationToken.None);
            job.Verified = lineResults.Count(l => l.Verified);
            job.State = upload.Failed == 0 && job.Verified >= job.Uploaded
                ? EmployeeSyncJobState.Completed
                : upload.Uploaded > 0
                  ? EmployeeSyncJobState.Completed
                  : EmployeeSyncJobState.Failed;
            job.LastError = upload.Error;
            job.LogLine =
                "Upload " +
                upload.Uploaded +
                " OK · verify " +
                job.Verified +
                "/" +
                Math.Max(upload.Uploaded, 1) +
                (upload.Failed > 0 ? " · " + upload.Failed + " failed" : "");
            if (upload.Failed > 0 && upload.Uploaded == 0)
                job.State = EmployeeSyncJobState.Failed;
        }
        catch (Exception ex)
        {
            job.State = EmployeeSyncJobState.Failed;
            job.LastError = ex.Message;
            job.LogLine = "Upload failed.";
        }

        await syncJobs.SaveJobAsync(job, null, isNew: false, CancellationToken.None);
    }

    private async Task RunDeleteJobAsync(Guid jobId, DeviceEndpoint ep, List<string> ids, int delayMs)
    {
        var job = syncJobs.GetJob(jobId);
        if (job is null) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IDeviceClient>();
            var del = await client.DeleteEmployeesAsync(ep, ids, delayMs, CancellationToken.None);
            job.LineResults = del.Lines.ToList();
            job.Deleted = del.Deleted;
            job.Failed = del.Failed;
            job.Total = del.Requested;
            job.State = del.Failed == 0 ? EmployeeSyncJobState.Completed : EmployeeSyncJobState.Failed;
            job.LastError = del.Error;
            job.LogLine = "Deleted " + del.Deleted + " of " + del.Requested;
        }
        catch (Exception ex)
        {
            job.State = EmployeeSyncJobState.Failed;
            job.LastError = ex.Message;
            job.LogLine = "Delete failed.";
        }

        await syncJobs.SaveJobAsync(job, null, isNew: false, CancellationToken.None);
    }

    [HttpPost("ResolveIds")]
    public IActionResult ResolveIds([FromBody] ResolveIdsRequest body)
    {
        var raw = body.Text ?? "";
        var ids = raw
            .Split(['\r', '\n', ',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(NicIdNormalizer.Normalize)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return Json(new { ok = true, count = ids.Count, ids });
    }

    private async Task<BiometricDevice?> ResolveTargetDeviceAsync(long? deviceId, CancellationToken cancellationToken)
    {
        if (deviceId is > 0)
            return await devices.GetByIdAsync(deviceId.Value, cancellationToken);
        return await selection.GetSelectedDeviceAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<BiometricDevice>> ResolveDevicesAsync(
        long? deviceId,
        bool allDevices,
        CancellationToken cancellationToken)
    {
        if (allDevices)
            return await devices.ListDevicesAsync(activeOnly: true, cancellationToken: cancellationToken);

        var dev = await ResolveTargetDeviceAsync(deviceId, cancellationToken);
        return dev is null ? [] : [dev];
    }

    private DeviceEndpoint ToEndpoint(BiometricDevice d) => new()
    {
        DeviceId = d.DeviceId,
        Host = d.DeviceIp,
        Port = d.DevicePort,
        RecordStart = _opts.RecordStart,
        RecordEnd = _opts.RecordEnd,
    };

    private async Task EnrichEnrollmentDatesAsync(
        List<DeviceEmployeeRecord> employees,
        CancellationToken cancellationToken)
    {
        if (employees.Count == 0) return;

        foreach (var group in employees.GroupBy(e => e.BioDeviceId))
        {
            var deviceId = group.Key is > 0 ? group.Key : null;
            var needPortal = group.Where(e => e.EnrolledAtUtc is null).Select(e => e.EmployeeId).ToList();
            if (needPortal.Count == 0) continue;

            var firstPunch = await attendance.GetFirstPunchByEmployeeIdsAsync(
                needPortal,
                deviceId,
                cancellationToken);

            foreach (var e in group)
            {
                if (e.EnrolledAtUtc is not null) continue;
                var nid = NicIdNormalizer.Normalize(e.EmployeeId);
                if (!firstPunch.TryGetValue(nid, out var dt)) continue;
                e.EnrolledAtUtc = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                e.EnrolledAtSource = "First attendance (portal)";
            }
        }
    }

    private async Task EnrichPhotosForPulledEmployeesAsync(
        IReadOnlyList<BiometricDevice> devices,
        List<DeviceEmployeeRecord> employees,
        int delayMs,
        CancellationToken cancellationToken)
    {
        if (employees.Count == 0) return;

        foreach (var group in employees.GroupBy(e => e.BioDeviceId))
        {
            if (group.Key is not > 0) continue;
            var dev = devices.FirstOrDefault(d => d.DeviceId == group.Key);
            if (dev is null) continue;
            var ep = ToEndpoint(dev);
            var needEnrich = group.Any(e =>
                !photoCache.TryGet(dev.DeviceId, NicIdNormalizer.Normalize(e.EmployeeId), out var b, out _)
                || b is null or { Length: 0 });
            if (!needEnrich) continue;
            await deviceClient.EnrichDisplayPhotosAsync(ep, group.ToList(), delayMs, cancellationToken);
        }
    }

    private object ToEmployeeRow(DeviceEmployeeRecord e)
    {
        var bio = EmployeeBiometricStatus.FromRecord(e);
        var id = NicIdNormalizer.Normalize(e.EmployeeId);
        var hasPhoto = e.BioDeviceId is > 0
                       && !string.IsNullOrEmpty(id)
                       && photoCache.TryGet(e.BioDeviceId.Value, id, out var bytes, out _)
                       && bytes is { Length: > 0 };
        string? photoUrl = null;
        if (hasPhoto && e.BioDeviceId is > 0)
        {
            photoUrl = Url.Action(
                "EmployeePhoto",
                "DeviceEmployeePhotos",
                new { deviceId = e.BioDeviceId.Value, employeeId = id });
        }

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
            enrolledAt = FormatEnrolledCell(e),
            enrolledAtSource = e.EnrolledAtSource,
            hasPhoto,
            photoUrl,
            photosPageUrl = Url.Action("Index", "DeviceEmployeePhotos", new { q = id }),
        };
    }

    private static DeviceEmployeeRecord ImportToDeviceRecord(EmployeeImportRow row) => new()
    {
        EmployeeId = row.EmployeeId,
        Name = row.Name ?? "",
        Department = row.Department,
        CardNumber = row.CardNumber,
        Authority = row.Authority ?? "0X11",
        CheckType = !string.IsNullOrWhiteSpace(row.FaceDataBase64) ? "face" : "face",
        FaceDataBase64 = row.FaceDataBase64,
    };

    private static object ToDiffJson(EmployeeDiffItem item) => new
    {
        item.EmployeeId,
        item.Name,
        item.Department,
        item.FaceTemplateCount,
        checkType = item.CheckType,
        biometricStatus = item.BiometricStatus.ToString(),
        biometricLabel = EmployeeBiometricStatus.ToLabel(item.BiometricStatus),
    };

    private static object ToJobJson(EmployeeSyncJob job) => new
    {
        jobId = job.JobId,
        jobKind = job.JobKind,
        state = job.State.ToString(),
        job.DeviceId,
        job.DeviceName,
        job.Total,
        job.Uploaded,
        job.Failed,
        job.Verified,
        job.Deleted,
        error = job.LastError,
        log = job.LogLine,
        updatedUtc = job.UpdatedUtc,
        lines = job.LineResults.Select(l => new
        {
            l.EmployeeId,
            l.Name,
            l.Succeeded,
            l.Verified,
            l.Method,
            error = l.Error,
        }),
    };

    private static string? FormatEnrolledCell(DeviceEmployeeRecord e)
    {
        if (e.EnrolledAtUtc is null) return null;
        var utc = e.EnrolledAtUtc.Value;
        var local = utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc;
        var text = local.ToString("yyyy-MM-dd HH:mm:ss");
        if (!string.IsNullOrWhiteSpace(e.EnrolledAtSource))
            text += " · " + e.EnrolledAtSource;
        return text;
    }

    private static (DateTime StartLocal, DateTime EndLocal) ResolveRecentEnrollWindow(int lookbackDays)
    {
        lookbackDays = Math.Clamp(lookbackDays, 1, 31);
        var today = DateTime.Now.Date;
        var start = today.AddDays(-(lookbackDays - 1));
        var end = today.AddDays(1).AddTicks(-1);
        return (start, end);
    }

    private static bool EmployeeEnrolledInWindow(DeviceEmployeeRecord e, DateTime startLocal, DateTime endLocal)
    {
        if (e.EnrolledAtUtc is null)
            return false;

        var local = e.EnrolledAtUtc.Value.Kind == DateTimeKind.Utc
            ? e.EnrolledAtUtc.Value.ToLocalTime()
            : DateTime.SpecifyKind(e.EnrolledAtUtc.Value, DateTimeKind.Unspecified);
        var d = local.Date;
        return d >= startLocal.Date && d <= endLocal.Date;
    }

    public sealed class PullBatchRequest
    {
        public long? DeviceId { get; set; }
        public bool AllDevices { get; set; }
        public int? DelayMs { get; set; }
        public List<string> Ids { get; set; } = [];
    }

    public sealed class PullAllRequest
    {
        public long? DeviceId { get; set; }
        public bool AllDevices { get; set; }
        public int? DelayMs { get; set; }
        public int? PushWaitSeconds { get; set; }
    }

    public sealed class PullRecentEnrolledRequest
    {
        public long? DeviceId { get; set; }
        public bool AllDevices { get; set; } = true;
        public int? LookbackDays { get; set; }
        public int? DelayMs { get; set; }
    }

    public sealed class ResolveIdsRequest
    {
        public string? Text { get; set; }
    }

    public sealed class ImportValidateRequest
    {
        public string? Text { get; set; }
        public List<ImportRowDto>? Rows { get; set; }
        public long? DeviceId { get; set; }
        public bool? SaveToRegistry { get; set; }
    }

    public sealed class ImportRowDto
    {
        public int Line { get; set; }
        public string? EmployeeId { get; set; }
        public string? Name { get; set; }
        public string? Department { get; set; }
        public string? CardNumber { get; set; }
        public string? Authority { get; set; }
        public string? FaceDataBase64 { get; set; }
    }

    public sealed class CompareRequest
    {
        public long? DeviceId { get; set; }
        public string? PortalSource { get; set; }
        public List<string>? PortalIds { get; set; }
        public int? DelayMs { get; set; }
    }

    public sealed class SyncStartRequest
    {
        public long? DeviceId { get; set; }
        public int? DelayMs { get; set; }
        public int? PushWaitSeconds { get; set; }
        public bool? SaveToRegistry { get; set; }
        public List<ImportRowDto>? Rows { get; set; }
    }

    public sealed class DeleteExtraRequest
    {
        public long? DeviceId { get; set; }
        public int? DelayMs { get; set; }
        public List<string>? EmployeeIds { get; set; }
    }

    public sealed class SyncCancelRequest
    {
        public Guid JobId { get; set; }
    }
}
