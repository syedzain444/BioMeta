using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Core.Utilities;
using BiometricDashboard.Web.Models;
using Microsoft.AspNetCore.Authorization;
using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BiometricDashboard.Web.Controllers;

[Route("Device/EnrollmentSync")]
[Authorize(Policy = "device_enroll_sync.page")]
public sealed class DeviceEnrollmentSyncController(
    IDeviceRepository devices,
    IDeviceClient deviceClient,
    IAttendanceRepository attendance,
    IEmployeeRegistryRepository employeeRegistry,
    IEmployeeSyncJobService syncJobs,
    ICurrentUserContext currentUser,
    IServiceScopeFactory scopeFactory,
    IOptions<DeviceOptions> deviceOptions) : Controller
{
    private readonly DeviceOptions _opts = deviceOptions.Value;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var all = await devices.ListDevicesAsync(activeOnly: true, cancellationToken);
        ViewBag.Devices = all;
        ViewBag.CanRun = CanRunSync(currentUser);

        ViewBag.PageBand = new PortalPageBandViewModel
        {
            Eyebrow = "HDCP · Copy enrollment",
            Title = "Device-to-device sync",
            IconClass = "bi-arrow-left-right",
            SubtitleMuted = "Source terminal → portal → target terminal (face template when available)",
            Stats =
            [
                new()
                {
                    Label = "Active devices",
                    Value = all.Count.ToString(),
                    Meta = "select source & target",
                    IconClass = "bi-hdd-network",
                    ColorIndex = 1,
                },
                new()
                {
                    Label = "Flow",
                    Value = "3 steps",
                    Meta = "compare · select · upload",
                    IconClass = "bi-diagram-2",
                    ColorIndex = 2,
                },
                new()
                {
                    Label = "Requirement",
                    Value = "Face data",
                    Meta = "GetEmployee on source",
                    IconClass = "bi-person-bounding-box",
                    ColorIndex = 3,
                },
            ],
        };

        return View();
    }

    [HttpPost("Preview")]
    [RequestTimeout(milliseconds: 900_000)]
    public async Task<IActionResult> Preview([FromBody] SyncPreviewRequest body, CancellationToken cancellationToken)
    {
        var (source, target, error) = await ResolvePairAsync(body.SourceDeviceId, body.TargetDeviceId, cancellationToken);
        if (error is not null)
            return Json(new { ok = false, error });

        var pushWait = body.PushWaitSeconds is > 0 and <= 300 ? body.PushWaitSeconds.Value : 240;
        var delay = body.DelayMs is > 0 and < 2000 ? body.DelayMs.Value : 120;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(14));

        var sourcePull = PullRosterAsync(source!, pushWait, delay, timeout.Token);
        var targetPull = PullRosterAsync(target!, pushWait, delay, timeout.Token);
        await Task.WhenAll(sourcePull, targetPull);
        var sourceMap = await sourcePull;
        var targetMap = await targetPull;

        if (sourceMap.Count == 0)
        {
            return Json(new
            {
                ok = false,
                error =
                    $"No users read from source ({source!.DeviceName}). Same fix as Device enrolled users: " +
                    "enable Push + User Data on the device, server IP = this portal PC (9920/9924), then compare again.",
            });
        }

        var targetIds = targetMap.Keys.ToHashSet(StringComparer.Ordinal);
        var toCopy = new List<object>();
        var alreadyOnBothRows = new List<object>();
        foreach (var kv in sourceMap.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var e = kv.Value;
            if (targetIds.Contains(kv.Key))
            {
                if (alreadyOnBothRows.Count < 80)
                {
                    alreadyOnBothRows.Add(new
                    {
                        employeeId = kv.Key,
                        name = e.Name,
                        department = e.Department,
                    });
                }

                continue;
            }

            var hasFace = HasFaceData(e);
            toCopy.Add(new
            {
                employeeId = kv.Key,
                name = e.Name,
                department = e.Department,
                cardNumber = e.CardNumber,
                hasFace,
                faceTemplateCount = e.FaceTemplateCount,
                biometricLabel = EmployeeBiometricStatus.ToLabel(EmployeeBiometricStatus.FromRecord(e)),
            });
        }

        var onBoth = sourceMap.Count - toCopy.Count;
        return Json(new
        {
            ok = true,
            source = new { source!.DeviceId, source.DeviceName, source.DeviceIp, count = sourceMap.Count },
            target = new { target!.DeviceId, target.DeviceName, target.DeviceIp, count = targetMap.Count },
            alreadyOnTarget = onBoth,
            missingOnTarget = toCopy.Count,
            rows = toCopy,
            alreadyOnBothRows,
            noDuplicateNote =
                "Only users missing on target are listed for copy. IDs already on target are skipped automatically.",
            compare = ToCompareJson(FromRosterMaps(sourceMap, targetMap)),
            warning = targetMap.Count == 0
                ? "Target list empty or incomplete — compare carefully before sync."
                : null,
        });
    }

    [HttpPost("Start")]
    [Authorize(Policy = "device_enroll_sync.run")]
    public async Task<IActionResult> Start([FromBody] SyncStartRequest body, CancellationToken cancellationToken)
    {
        var (source, target, error) = await ResolvePairAsync(body.SourceDeviceId, body.TargetDeviceId, cancellationToken);
        if (error is not null)
            return Json(new { ok = false, error });

        var ids = (body.EmployeeIds ?? [])
            .Select(NicIdNormalizer.Normalize)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .Take(300)
            .ToList();
        if (ids.Count == 0)
            return Json(new { ok = false, error = "Select at least one employee to copy." });

        var delay = body.DelayMs is > 0 and < 2000 ? body.DelayMs.Value : 120;
        var pushWait = body.PushWaitSeconds is > 0 and <= 300 ? body.PushWaitSeconds.Value : 240;

        var requested = ids.Count;
        var skippedDup = 0;
        if (body.SkipTargetRosterCheck == true && body.CompareBefore is not null)
        {
            // Preview listed missing-on-target only — skip slow target re-pull.
        }
        else
        {
            using var dupCheck = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dupCheck.CancelAfter(TimeSpan.FromMinutes(4));
            var targetMap = await PullRosterAsync(target!, pushWait, delay, dupCheck.Token);
            var onTarget = targetMap.Keys.ToHashSet(StringComparer.Ordinal);
            ids = ids.Where(id => !onTarget.Contains(id)).ToList();
            skippedDup = requested - ids.Count;
        }
        if (ids.Count == 0)
        {
            return Json(new
            {
                ok = false,
                error = skippedDup > 0
                    ? $"All {skippedDup} selected user(s) are already on {target!.DeviceName} — duplicate copy blocked."
                    : "No users to copy.",
                skippedDuplicates = skippedDup,
            });
        }

        var job = syncJobs.CreateJob(target!.DeviceId, target.DeviceIp, target.DeviceName, [], "device-sync");
        job.SourceDeviceId = source!.DeviceId;
        job.SourceDeviceName = source.DeviceName;
        job.SkippedAlreadyOnTarget = skippedDup;
        job.CompareBefore = body.CompareBefore is not null
            ? MapCompare(body.CompareBefore)
            : null;
        job.State = EmployeeSyncJobState.Queued;
        job.Total = ids.Count;
        job.LogLine =
            "Queued copy from " + source.DeviceName + " → " + target.DeviceName + " (" + ids.Count + " user(s)"
            + (skippedDup > 0 ? ", " + skippedDup + " skipped (already on target)" : "") + ").";
        await syncJobs.SaveJobAsync(job, currentUser.UserId, isNew: true, cancellationToken);

        var jobId = job.JobId;
        var sourceEp = ToEndpoint(source);
        var targetEp = ToEndpoint(target);
        var userId = currentUser.UserId;
        var saveRegistry = body.SaveToRegistry != false;

        _ = RunDeviceSyncJobAsync(jobId, source, target, sourceEp, targetEp, ids, delay, pushWait, saveRegistry, userId);

        return Json(new
        {
            ok = true,
            jobId,
            total = ids.Count,
            skippedDuplicates = skippedDup,
            requested,
        });
    }

    [HttpGet("Job/{jobId:guid}")]
    public async Task<IActionResult> JobStatus(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            var job = syncJobs.GetJob(jobId) ?? await syncJobs.GetJobAsync(jobId, cancellationToken);
            if (job is null)
                return Json(new { ok = false, error = "Job not found." });
            return Json(new { ok = true, job = ToJobJson(job) });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = "Could not load job status.", detail = ex.Message });
        }
    }

    private async Task RunDeviceSyncJobAsync(
        Guid jobId,
        BiometricDevice source,
        BiometricDevice target,
        DeviceEndpoint sourceEp,
        DeviceEndpoint targetEp,
        List<string> ids,
        int delayMs,
        int pushWaitSeconds,
        bool saveRegistry,
        long? userId)
    {
        var job = syncJobs.GetJob(jobId);
        if (job is null) return;

        try
        {
            job.State = EmployeeSyncJobState.TriggeringDevice;
            job.LogLine = "Reading " + ids.Count + " user(s) from " + source.DeviceName + "…";
            await syncJobs.SaveJobAsync(job, userId, isNew: false, CancellationToken.None);

            using var scope = scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IDeviceClient>();

            sourceEp.DeviceId = source.DeviceId;
            var readDelay = Math.Clamp(delayMs, 35, 120);
            var pull = await client.PullEmployeesAsync(sourceEp, ids, readDelay, CancellationToken.None);
            if (pull.Employees.Count == 0)
            {
                job.State = EmployeeSyncJobState.Failed;
                job.LastError = pull.Error ?? "Could not read users from source device.";
                job.LogLine = job.LastError;
                await syncJobs.SaveJobAsync(job, userId, isNew: false, CancellationToken.None);
                return;
            }

            var enriched = pull.Employees.ToList();
            await client.EnrichFaceTemplatesAsync(sourceEp, enriched, readDelay, CancellationToken.None);
            job.LogLine = "Loaded " + enriched.Count + " user(s) from source (face templates).";
            syncJobs.UpdateJob(job);

            var importRows = enriched.Select((e, idx) => new EmployeeImportRow
            {
                Line = idx + 1,
                EmployeeId = e.EmployeeId,
                Name = e.Name,
                Department = e.Department,
                CardNumber = e.CardNumber,
                Authority = e.Authority,
                FaceDataBase64 = e.FaceDataBase64,
            }).ToList();

            var skippedNoFace = importRows.Count(r => string.IsNullOrWhiteSpace(r.FaceDataBase64));
            importRows = importRows.Where(r => !string.IsNullOrWhiteSpace(r.FaceDataBase64)).ToList();
            if (importRows.Count == 0)
            {
                job.State = EmployeeSyncJobState.Failed;
                job.LastError = "No face template on source for selected user(s). Re-enroll on source or pick other IDs.";
                job.LogLine = job.LastError;
                await syncJobs.SaveJobAsync(job, userId, isNew: false, CancellationToken.None);
                return;
            }

            var validation = EmployeeImportValidator.Validate(importRows);
            var valid = validation.Rows.Where(r => r.IsValid).ToList();
            if (valid.Count == 0)
            {
                job.State = EmployeeSyncJobState.Failed;
                job.LastError = "No valid rows after validation.";
                await syncJobs.SaveJobAsync(job, userId, isNew: false, CancellationToken.None);
                return;
            }

            if (saveRegistry)
            {
                var regRepo = scope.ServiceProvider.GetRequiredService<IEmployeeRegistryRepository>();
                await regRepo.UpsertManyAsync(valid, target.DeviceId, userId, "device-to-device-sync", CancellationToken.None);
            }

            job.State = EmployeeSyncJobState.Uploading;
            job.LogLine = "Uploading " + valid.Count + " user(s) to " + target.DeviceName + "…" +
                          (skippedNoFace > 0 ? " (" + skippedNoFace + " skipped — no face)" : "");
            job.Total = valid.Count;
            await syncJobs.SaveJobAsync(job, userId, isNew: false, CancellationToken.None);

            targetEp.DeviceId = target.DeviceId;
            var employees = valid.Select(ImportToDeviceRecord).ToList();
            var uploadDelay = Math.Clamp(delayMs, 50, 150);
            var uploadPushWait = Math.Clamp(pushWaitSeconds, 60, 120);
            var upload = await client.UploadEmployeesAsync(
                targetEp,
                jobId,
                employees,
                uploadDelay,
                uploadPushWait,
                CancellationToken.None,
                onProgress: p =>
                {
                    job.LogLine =
                        "Uploading " + p.Index + "/" + p.Total + " → " + target.DeviceName +
                        " · " + p.Phase + " · " + p.EmployeeId +
                        (p.UploadedSoFar > 0 ? " · OK so far: " + p.UploadedSoFar : "");
                    job.Uploaded = p.UploadedSoFar;
                    syncJobs.UpdateJob(job);
                    _ = syncJobs.SaveJobAsync(job, userId, isNew: false, CancellationToken.None);
                });

            job.Uploaded = upload.Uploaded;
            job.Failed = upload.Failed;
            job.Total = upload.Requested;
            job.LineResults = upload.Lines.ToList();
            var lineResults = job.LineResults.ToList();
            var needVerify = lineResults.Any(l => l.Succeeded && !l.Verified);
            if (needVerify)
            {
                job.State = EmployeeSyncJobState.Verifying;
                job.LogLine = "Quick verify on " + target.DeviceName + "…";
                syncJobs.UpdateJob(job);
                await client.VerifyEmployeesOnDeviceAsync(targetEp, lineResults, Math.Min(uploadDelay, 50), CancellationToken.None);
            }

            job.LineResults = lineResults;
            job.Verified = job.LineResults.Count(l => l.Verified);
            job.State = upload.Failed == 0 && job.Verified >= job.Uploaded
                ? EmployeeSyncJobState.Completed
                : upload.Uploaded > 0
                    ? EmployeeSyncJobState.Completed
                    : EmployeeSyncJobState.Failed;
            var firstLineErr = upload.Lines.FirstOrDefault(l => !l.Succeeded)?.Error;
            job.LastError = upload.Uploaded > 0
                ? upload.Error
                : upload.Error ?? firstLineErr ?? "Target device rejected upload (HDCP). Check push sync on target.";
            job.LogLine =
                "Done · uploaded " + upload.Uploaded + " · verified " + job.Verified + "/" + Math.Max(job.Total, 1) +
                (upload.Failed > 0 ? " · failed " + upload.Failed : "");
            if (upload.Uploaded == 0)
                job.State = EmployeeSyncJobState.Failed;
            else if (upload.Failed > 0)
                job.State = EmployeeSyncJobState.Completed;

            if (upload.Uploaded > 0 && job.CompareBefore is not null)
            {
                job.CompareAfter = EstimateCompareAfter(job.CompareBefore, job.Uploaded);
                job.LogLine = job.LogLine + " · Comparison summary updated.";
            }
        }
        catch (Exception ex)
        {
            job.State = EmployeeSyncJobState.Failed;
            job.LastError = ex.Message;
            job.LogLine = "Sync failed.";
        }
        finally
        {
            await syncJobs.SaveJobAsync(job, userId, isNew: false, CancellationToken.None);
        }
    }

    private async Task<Dictionary<string, DeviceEmployeeRecord>> PullRosterAsync(
        BiometricDevice dev,
        int pushWait,
        int delay,
        CancellationToken cancellationToken)
    {
        var ep = ToEndpoint(dev);
        ep.DeviceId = dev.DeviceId;
        var pull = await deviceClient.PullAllEnrolledAsync(ep, delay, pushWait, cancellationToken, enrichPhotos: false);
        var map = new Dictionary<string, DeviceEmployeeRecord>(StringComparer.Ordinal);
        foreach (var e in pull.Employees)
        {
            var id = NicIdNormalizer.Normalize(e.EmployeeId);
            if (string.IsNullOrEmpty(id)) continue;
            e.EmployeeId = id;
            map[id] = e;
        }

        var needSupplement = map.Count == 0
            || !pull.IsComplete
            || (pull.DeviceEnrolledCount > 0 && map.Count < pull.DeviceEnrolledCount);
        if (needSupplement)
        {
            var portalIds = await attendance.GetDistinctEmployeeIdsAsync(dev.DeviceId, cancellationToken: cancellationToken);
            var registryIds = await employeeRegistry.ListEmployeeIdsAsync(dev.DeviceId, cancellationToken);
            var supplementalIds = portalIds
                .Concat(registryIds)
                .Select(NicIdNormalizer.Normalize)
                .Where(x => !string.IsNullOrEmpty(x) && !map.ContainsKey(x))
                .Distinct(StringComparer.Ordinal)
                .Take(10_000)
                .ToList();

            if (supplementalIds.Count > 0)
            {
                var viaIds = await deviceClient.PullEmployeesAsync(ep, supplementalIds, delay, cancellationToken);
                foreach (var e in viaIds.Employees)
                {
                    var id = NicIdNormalizer.Normalize(e.EmployeeId);
                    if (string.IsNullOrEmpty(id) || map.ContainsKey(id)) continue;
                    e.EmployeeId = id;
                    e.BioDeviceId = dev.DeviceId;
                    map[id] = e;
                }
            }
        }

        return map;
    }

    private async Task<(BiometricDevice? Source, BiometricDevice? Target, string? Error)> ResolvePairAsync(
        long sourceId,
        long targetId,
        CancellationToken cancellationToken)
    {
        if (sourceId <= 0 || targetId <= 0)
            return (null, null, "Select source and target devices.");
        if (sourceId == targetId)
            return (null, null, "Source and target must be different devices.");

        var source = await devices.GetByIdAsync(sourceId, cancellationToken);
        var target = await devices.GetByIdAsync(targetId, cancellationToken);
        if (source is null || target is null)
            return (null, null, "Device not found.");
        if (!source.IsActive || !target.IsActive)
            return (null, null, "Both devices must be active.");

        return (source, target, null);
    }

    private static bool HasFaceData(DeviceEmployeeRecord e) =>
        !string.IsNullOrWhiteSpace(e.FaceDataBase64) || e.FaceTemplateCount > 0;

    private DeviceEndpoint ToEndpoint(BiometricDevice d) => new()
    {
        DeviceId = d.DeviceId,
        Host = d.DeviceIp,
        Port = d.DevicePort,
        RecordStart = _opts.RecordStart,
        RecordEnd = _opts.RecordEnd,
    };

    private static DeviceEmployeeRecord ImportToDeviceRecord(EmployeeImportRow row) => new()
    {
        EmployeeId = row.EmployeeId,
        Name = row.Name ?? "",
        Department = row.Department,
        CardNumber = row.CardNumber,
        Authority = row.Authority ?? "0X11",
        CheckType = "face",
        FaceDataBase64 = row.FaceDataBase64,
    };

    private static object ToJobJson(EmployeeSyncJob job)
    {
        const int maxLinesInJson = 150;
        var lines = job.LineResults;
        var lineSlice = lines.Count <= maxLinesInJson
            ? lines
            : lines.Take(maxLinesInJson).ToList();
        return new
        {
            jobId = job.JobId,
            state = job.State.ToString(),
            job.DeviceId,
            job.DeviceName,
            job.Total,
            uploaded = job.Uploaded,
            failed = job.Failed,
            job.Verified,
            error = job.LastError,
            log = job.LogLine,
            updatedUtc = job.UpdatedUtc,
            progress = job.Total > 0
                ? Math.Min(100, (int)Math.Round(100.0 * (job.Uploaded + job.Failed + job.Verified) / Math.Max(job.Total, 1)))
                : 0,
            lines = lineSlice.Select(l => new
            {
                l.EmployeeId,
                l.Name,
                l.Succeeded,
                l.Verified,
                error = TruncateForJson(l.Error, 500),
            }),
            linesTruncated = lines.Count > maxLinesInJson,
            totalLines = lines.Count,
            skippedAlreadyOnTarget = job.SkippedAlreadyOnTarget,
            sourceDeviceName = job.SourceDeviceName,
            compareBefore = ToCompareJson(job.CompareBefore),
            compareAfter = ToCompareJson(job.CompareAfter),
        };
    }

    private static string? TruncateForJson(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLen) return value;
        return value[..maxLen] + "…";
    }

    private static bool IsEmptyCompareSnapshot(DeviceSyncCompareSnapshot c) =>
        c.SourceCount == 0 && c.TargetCount == 0 && c.MissingOnTarget == 0 && c.AlreadyOnBoth == 0;

    private static DeviceSyncCompareSnapshot EstimateCompareAfter(DeviceSyncCompareSnapshot before, int uploaded)
    {
        uploaded = Math.Max(0, uploaded);
        return new DeviceSyncCompareSnapshot
        {
            SourceCount = before.SourceCount,
            TargetCount = before.TargetCount + uploaded,
            MissingOnTarget = Math.Max(0, before.MissingOnTarget - uploaded),
            AlreadyOnBoth = before.AlreadyOnBoth + uploaded,
        };
    }

    private static DeviceSyncCompareSnapshot FromRosterMaps(
        Dictionary<string, DeviceEmployeeRecord> sourceMap,
        Dictionary<string, DeviceEmployeeRecord> targetMap)
    {
        var targetIds = targetMap.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = sourceMap.Keys.Count(k => !targetIds.Contains(k));
        var onBoth = sourceMap.Count - missing;
        return new DeviceSyncCompareSnapshot
        {
            SourceCount = sourceMap.Count,
            TargetCount = targetMap.Count,
            MissingOnTarget = missing,
            AlreadyOnBoth = onBoth,
        };
    }

    private static object? ToCompareJson(DeviceSyncCompareSnapshot? c) =>
        c is null
            ? null
            : new
            {
                sourceCount = c.SourceCount,
                targetCount = c.TargetCount,
                missingOnTarget = c.MissingOnTarget,
                alreadyOnBoth = c.AlreadyOnBoth,
            };

    private static DeviceSyncCompareSnapshot MapCompare(SyncCompareDto dto) => new()
    {
        SourceCount = dto.SourceCount,
        TargetCount = dto.TargetCount,
        MissingOnTarget = dto.MissingOnTarget,
        AlreadyOnBoth = dto.AlreadyOnBoth,
    };

    public sealed class SyncPreviewRequest
    {
        public long SourceDeviceId { get; set; }
        public long TargetDeviceId { get; set; }
        public int? DelayMs { get; set; }
        public int? PushWaitSeconds { get; set; }
    }

    private static bool CanRunSync(ICurrentUserContext user) =>
        user.HasPermission("device_enroll_sync.run")
        || user.HasPermission("devices.manage")
        || user.HasPermission("device_enroll_hub.save");

    public sealed class SyncStartRequest
    {
        public long SourceDeviceId { get; set; }
        public long TargetDeviceId { get; set; }
        public List<string>? EmployeeIds { get; set; }
        public int? DelayMs { get; set; }
        public int? PushWaitSeconds { get; set; }
        public bool? SaveToRegistry { get; set; }
        public SyncCompareDto? CompareBefore { get; set; }
        /// <summary>When true and compare snapshot was sent, skip re-reading full target roster before copy.</summary>
        public bool? SkipTargetRosterCheck { get; set; }
    }

    public sealed class SyncCompareDto
    {
        public int SourceCount { get; set; }
        public int TargetCount { get; set; }
        public int MissingOnTarget { get; set; }
        public int AlreadyOnBoth { get; set; }
    }
}