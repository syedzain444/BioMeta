using System.Diagnostics;
using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BiometricDashboard.Infrastructure.Device;

public sealed class HanwangDeviceClient(
    IOptions<DeviceOptions> options,
    DevicePushSyncCoordinator pushSync,
    DeviceEmployeeDownloadCoordinator downloadSync,
    IEmployeePhotoCache photoCache,
    ILogger<HanwangDeviceClient> logger) : IDeviceClient
{
    private readonly DeviceOptions _defaults = options.Value;

    public async Task<DeviceSnapshot> GetDeviceSnapshotAsync(
        DeviceEndpoint endpoint,
        CancellationToken cancellationToken = default,
        int commandMaxAttempts = 2)
    {
        var snap = new DeviceSnapshot { CheckedAt = DateTime.UtcNow };
        try
        {
            var raw = await ExecuteWithRetryAsync(
                endpoint, "GetDeviceInfo()", TimeSpan.FromSeconds(20), cancellationToken, commandMaxAttempts);
            if (raw.Contains("Device Is Busy", StringComparison.Ordinal))
            {
                snap.LastError = "Device Is Busy (stop Python scan / other 9922 tools, wait a few seconds)";
                return snap;
            }

            if (!raw.Contains("success", StringComparison.OrdinalIgnoreCase))
            {
                snap.LastError = raw.Length > 200 ? raw[..200] : raw;
                return snap;
            }

            ApplyInfoFields(snap, HanwangProtocolParser.ParseFields(raw.Replace('\n', ' ')));
        }
        catch (Exception ex)
        {
            snap.IsOnline = false;
            snap.LastError = ex.Message;
            logger.LogWarning(ex, "Device snapshot failed for {Host}:{Port}", endpoint.Host, endpoint.Port);
        }

        return snap;
    }

    public Task<AttendancePullResult> PullAttendanceAsync(
        DeviceEndpoint endpoint,
        CancellationToken cancellationToken = default,
        int commandMaxAttempts = 2) =>
        PullAttendanceAsync(endpoint, endpoint.RecordStart, endpoint.RecordEnd, cancellationToken, commandMaxAttempts);

    public async Task<AttendancePullResult> PullAttendanceAsync(
        DeviceEndpoint endpoint,
        string startTime,
        string endTime,
        CancellationToken cancellationToken = default,
        int commandMaxAttempts = 2)
    {
        var empty = new AttendancePullResult();
        try
        {
            var cmd = $"GetRecord(start_time=\"{startTime}\" end_time=\"{endTime}\")";
            var raw = await ExecuteWithRetryAsync(
                endpoint, cmd, TimeSpan.FromSeconds(45), cancellationToken, commandMaxAttempts);
            if (!raw.Contains("success", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("GetRecord failed on {Host}: {Raw}", endpoint.Host, raw.Length > 120 ? raw[..120] : raw);
                return empty;
            }

            var (header, rows) = HanwangProtocolParser.ParseReturn(raw);
            var flat = raw.Replace('\n', ' ');
            var fields = HanwangProtocolParser.ParseFields(flat);
            foreach (var kv in header)
                fields[kv.Key] = kv.Value;

            var serial = fields.GetValueOrDefault("dev_id") ?? "";
            int? total = int.TryParse(fields.GetValueOrDefault("total"), out var t) ? t : null;

            var list = new List<AttendanceRecord>();
            foreach (var row in rows)
            {
                var rec = HanwangProtocolParser.ToAttendance(row, serial);
                if (rec is null) continue;
                rec.BioDeviceId = endpoint.DeviceId;
                list.Add(rec);
            }

            return new AttendancePullResult
            {
                Records = list,
                RecordTotalOnDevice = total,
                DeviceSerial = serial,
                Succeeded = true,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fetch attendance failed for {Host}", endpoint.Host);
            return empty;
        }
    }

    private static void ApplyInfoFields(DeviceSnapshot snap, Dictionary<string, string> fields)
    {
        snap.IsOnline = true;
        snap.SerialNumber = fields.GetValueOrDefault("dev_id");
        snap.Firmware = fields.GetValueOrDefault("edition");
        snap.DeviceType = fields.GetValueOrDefault("type");
        _ = int.TryParse(fields.GetValueOrDefault("real_employee"), out var emp);
        _ = int.TryParse(fields.GetValueOrDefault("real_faceregist"), out var face);
        _ = int.TryParse(fields.GetValueOrDefault("real_facerecord"), out var rec);
        snap.EmployeeCount = emp;
        snap.FaceEnrolled = face;
        snap.RecordCountOnDevice = rec;
    }

    public async Task<DeviceEmployeeRecord?> GetEmployeeAsync(
        DeviceEndpoint endpoint,
        string employeeId,
        CancellationToken cancellationToken = default)
    {
        var id = NicIdNormalizer.Normalize(employeeId);
        if (string.IsNullOrEmpty(id)) return null;

        var raw = await ExecuteWithRetryAsync(
            endpoint,
            $"GetEmployee(employee_id=\"{id}\")",
            TimeSpan.FromSeconds(45),
            cancellationToken);
        TryCachePhotoFromRaw(endpoint, id, raw);
        return HanwangProtocolParser.ToDeviceEmployee(raw, id);
    }

    public async Task<DeviceEmployeeLookupResult> LookupEmployeeWithPhotoAsync(
        DeviceEndpoint endpoint,
        string employeeIdOrQuery,
        CancellationToken cancellationToken = default)
    {
        var id = NicIdNormalizer.Normalize(employeeIdOrQuery);
        if (string.IsNullOrEmpty(id))
        {
            return new DeviceEmployeeLookupResult
            {
                Found = false,
                Error = "Enter a valid employee ID (NIC).",
            };
        }

        var commands = new[]
        {
            $"GetEmployee(employee_id=\"{id}\")",
            $"GetEmployee(id=\"{id}\")",
            $"GetEmployee(employee_id=\"{id}\" photo=\"1\")",
            $"GetEmployeePhoto(employee_id=\"{id}\")",
            $"GetPhoto(employee_id=\"{id}\")",
        };

        string? lastRaw = null;
        DeviceEmployeeRecord? emp = null;
        string? method = null;
        DecodedEmployeePhoto? decoded = null;

        foreach (var cmd in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await ExecuteWithRetryAsync(
                endpoint, cmd, TimeSpan.FromSeconds(50), cancellationToken);
            lastRaw = raw;
            if (raw.Contains("Not Support", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
                continue;
            var candidate = HanwangProtocolParser.ToDeviceEmployee(raw, id);
            if (candidate is null) continue;

            emp = candidate;
            method = cmd;
            decoded = HanwangEmployeePhotoDecoder.TryExtractFromEmployeeResponse(raw);
            if (decoded is not null) break;
        }

        if (emp is not null && decoded is not null && endpoint.DeviceId > 0)
            photoCache.Store(endpoint.DeviceId, id, decoded.Bytes, decoded.ContentType);

        if (emp is not null && decoded is not null)
        {
            emp.BioDeviceId = endpoint.DeviceId;
            return new DeviceEmployeeLookupResult
            {
                Found = true,
                Employee = emp,
                HasDisplayPhoto = true,
                PhotoHint = decoded.Source,
                HdcpMethod = method,
            };
        }

        if (emp is null)
        {
            return new DeviceEmployeeLookupResult
            {
                Found = false,
                Error = lastRaw is { Length: > 0 }
                    ? (lastRaw.Length > 240 ? lastRaw[..240] : lastRaw)
                    : "Employee not found on device.",
            };
        }

        emp.BioDeviceId = endpoint.DeviceId;
        var hint = emp.FaceTemplateCount > 0 || !string.IsNullOrWhiteSpace(emp.FaceDataBase64)
            ? "Face template on device (face_data) — not a display photo. Enable 大头照 on the terminal or re-enroll with photo."
            : "No photo_data in GetEmployee response.";

        return new DeviceEmployeeLookupResult
        {
            Found = true,
            Employee = emp,
            HasDisplayPhoto = false,
            PhotoHint = hint,
            HdcpMethod = method,
        };
    }

    public async Task<DeviceEmployeePullResult> PullEmployeesAsync(
        DeviceEndpoint endpoint,
        IEnumerable<string> employeeIds,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var ids = employeeIds
            .Select(NicIdNormalizer.Normalize)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new DeviceEmployeePullResult { Requested = ids.Count };
        if (ids.Count == 0)
        {
            result.Succeeded = true;
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        try
        {
            using (await DeviceConnectionLock.AcquireAsync(endpoint, cancellationToken))
            {
                var enrolled = 0;
                using var tcp = new HanwangTcpClient(endpoint.Host, endpoint.Port);
                await tcp.ConnectAsync(TimeSpan.FromSeconds(15), cancellationToken);

                var infoRaw = await ExecuteOnSessionWithBusyRetryAsync(
                    tcp, endpoint, "GetDeviceInfo()", TimeSpan.FromSeconds(15), cancellationToken);
                if (infoRaw.Contains("success", StringComparison.OrdinalIgnoreCase))
                {
                    var infoFields = HanwangProtocolParser.ParseFields(infoRaw.Replace('\n', ' '));
                    _ = int.TryParse(infoFields.GetValueOrDefault("real_employee"), out enrolled);
                }

                result.DeviceEnrolledCount = enrolled;
                var found = new List<DeviceEmployeeRecord>();

                for (var i = 0; i < ids.Count; i++)
                {
                    var id = ids[i];
                    cancellationToken.ThrowIfCancellationRequested();
                    var raw = await ExecuteOnSessionWithBusyRetryAsync(
                        tcp,
                        endpoint,
                        $"GetEmployee(employee_id=\"{id}\")",
                        TimeSpan.FromSeconds(45),
                        cancellationToken);
                    var emp = HanwangProtocolParser.ToDeviceEmployee(raw, id);
                    TryCachePhotoFromRaw(endpoint, id, raw);
                    if (emp is not null)
                    {
                        emp.EmployeeId = NicIdNormalizer.Normalize(emp.EmployeeId);
                        emp.BioDeviceId = endpoint.DeviceId;
                        found.Add(emp);
                    }

                    if (delayMsBetweenCalls > 0 && i < ids.Count - 1)
                        await Task.Delay(delayMsBetweenCalls, cancellationToken);
                }

                result.Employees = found;
                result.Found = found.Count;
                result.Succeeded = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PullEmployees failed for {Host}", endpoint.Host);
            result.Error = ex.Message;
        }

        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    public async Task<IReadOnlyList<string>> GetEmployeeIdsFromDeviceAttendanceAsync(
        DeviceEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        var pull = await PullAttendanceAsync(endpoint, cancellationToken);
        return UniqueEmployeeIdsFromAttendance(pull);
    }

    public async Task<IReadOnlyList<string>> GetEmployeeIdsFromDeviceRecordsInRangeAsync(
        DeviceEndpoint endpoint,
        DateTime startLocal,
        DateTime endLocal,
        CancellationToken cancellationToken = default)
    {
        var fmt = "yyyy-MM-dd HH:mm:ss";
        var pull = await PullAttendanceAsync(
            endpoint,
            startLocal.ToString(fmt),
            endLocal.ToString(fmt),
            cancellationToken);
        return UniqueEmployeeIdsFromAttendance(pull);
    }

    private static IReadOnlyList<string> UniqueEmployeeIdsFromAttendance(AttendancePullResult pull)
    {
        if (!pull.Succeeded)
            return [];

        return pull.Records
            .Select(r => NicIdNormalizer.Normalize(r.EmployeeId))
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    public async Task EnrichFaceTemplatesAsync(
        DeviceEndpoint endpoint,
        IList<DeviceEmployeeRecord> employees,
        int delayMsBetweenCalls = 80,
        CancellationToken cancellationToken = default)
    {
        var need = employees
            .Where(e => string.IsNullOrWhiteSpace(e.FaceDataBase64) && e.FaceTemplateCount <= 0)
            .ToList();
        if (need.Count == 0) return;

        try
        {
            using (await DeviceConnectionLock.AcquireAsync(endpoint, cancellationToken))
            {
                using var tcp = new HanwangTcpClient(endpoint.Host, endpoint.Port);
                await tcp.ConnectAsync(TimeSpan.FromSeconds(12), cancellationToken);

                for (var i = 0; i < need.Count; i++)
                {
                    var emp = need[i];
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = NicIdNormalizer.Normalize(emp.EmployeeId);
                    if (string.IsNullOrEmpty(id)) continue;

                    foreach (var cmd in new[]
                             {
                                 $"GetEmployee(employee_id=\"{id}\")",
                                 $"GetEmployee(id=\"{id}\")",
                             })
                    {
                        var raw = await ExecuteOnSessionWithBusyRetryAsync(
                            tcp, endpoint, cmd, TimeSpan.FromSeconds(35), cancellationToken, maxAttempts: 2);
                        var parsed = HanwangProtocolParser.ToDeviceEmployee(raw, id);
                        if (parsed is null) continue;
                        if (!string.IsNullOrWhiteSpace(parsed.FaceDataBase64))
                            emp.FaceDataBase64 = parsed.FaceDataBase64;
                        if (parsed.FaceTemplateCount > 0)
                            emp.FaceTemplateCount = parsed.FaceTemplateCount;
                        if (string.IsNullOrWhiteSpace(emp.Name))
                            emp.Name = parsed.Name;
                        if (string.IsNullOrWhiteSpace(emp.Department))
                            emp.Department = parsed.Department;
                        if (!string.IsNullOrWhiteSpace(emp.FaceDataBase64) || emp.FaceTemplateCount > 0)
                            break;
                    }

                    if (delayMsBetweenCalls > 0 && i < need.Count - 1)
                        await Task.Delay(delayMsBetweenCalls, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EnrichFaceTemplates failed for {Host}", endpoint.Host);
        }
    }

    public async Task<DeviceEmployeePullResult> PullAllEnrolledAsync(
        DeviceEndpoint endpoint,
        int delayMsBetweenCalls = 120,
        int pushWaitSeconds = 90,
        CancellationToken cancellationToken = default,
        bool enrichPhotos = true,
        bool fastRosterPull = false)
    {
        var sw = Stopwatch.StartNew();
        var result = new DeviceEmployeePullResult();
        if (endpoint.DeviceId <= 0)
        {
            result.Error = "Device id is required.";
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        var deviceId = endpoint.DeviceId;
        var merged = new Dictionary<string, DeviceEmployeeRecord>(StringComparer.Ordinal);
        string? method = null;
        var target = 0;

        void Absorb(IEnumerable<DeviceEmployeeRecord> list, string tag)
        {
            foreach (var e in list)
            {
                var id = NicIdNormalizer.Normalize(e.EmployeeId);
                if (string.IsNullOrEmpty(id)) continue;
                e.EmployeeId = id;
                e.BioDeviceId = deviceId;
                merged[id] = e;
            }

            if (merged.Count > 0 && method is null)
                method = tag;
        }

        try
        {
            using (await DeviceConnectionLock.AcquireAsync(endpoint, cancellationToken, TimeSpan.FromMinutes(3)))
            {
                using var tcp = new HanwangTcpClient(endpoint.Host, endpoint.Port);
                await tcp.ConnectAsync(TimeSpan.FromSeconds(15), cancellationToken);

                var infoRaw = await ExecuteOnSessionWithBusyRetryAsync(
                    tcp, endpoint, "GetDeviceInfo()", TimeSpan.FromSeconds(15), cancellationToken);
                if (infoRaw.Contains("success", StringComparison.OrdinalIgnoreCase))
                {
                    var infoFields = HanwangProtocolParser.ParseFields(infoRaw.Replace('\n', ' '));
                    target = await ResolveEmployeeTargetCountAsync(tcp, endpoint, infoFields, cancellationToken);
                }

                Absorb(await TryPullBulkListAsync(tcp, endpoint, cancellationToken), "bulk-list");

                if (target <= 0 || merged.Count < target)
                    Absorb(await TryPullByIndexAsync(tcp, endpoint, target, delayMsBetweenCalls, exhaustive: false, cancellationToken), "by-index");

                var needDeepIndex = target > 0 && merged.Count < target
                    && (!fastRosterPull || merged.Count < Math.Max(1, (int)(target * 0.85)));
                if (needDeepIndex)
                    Absorb(await TryPullByIndexAsync(tcp, endpoint, target, delayMsBetweenCalls, exhaustive: true, cancellationToken), "by-index-deep");

                var pushWait = fastRosterPull ? Math.Min(pushWaitSeconds, 95) : pushWaitSeconds;
                if (target <= 0 || merged.Count < target)
                {
                    var tag = await RunPushEmployeeDownloadAsync(
                        tcp, endpoint, deviceId, target, pushWait, merged, cancellationToken);
                    if (tag is not null && method is null) method = tag;
                }

                if (!fastRosterPull && target > 0 && merged.Count < target)
                {
                    var tag = await RunPushEmployeeDownloadAsync(
                        tcp, endpoint, deviceId, target,
                        Math.Max(pushWaitSeconds, 180),
                        merged,
                        cancellationToken,
                        extraTriggers: true);
                    if (tag is not null) method = tag;
                }
            }

            var needAttendanceFallback = !fastRosterPull && (merged.Count < target || (target == 0 && merged.Count == 0));
            if (needAttendanceFallback)
                await TryPullViaAttendanceIdsAsync(
                    endpoint, merged, delayMsBetweenCalls, cancellationToken, tag => { if (method is null) method = tag; });

            var ordered = merged.Values.OrderBy(e => e.EmployeeId, StringComparer.Ordinal).ToList();
            if (enrichPhotos)
                await EnrichDisplayPhotosAsync(endpoint, ordered, delayMsBetweenCalls, cancellationToken);

            result.DeviceEnrolledCount = target;
            result.Employees = ordered;
            result.Found = result.Employees.Count;
            result.Requested = target > 0 ? target : result.Found;
            result.Method = method ?? (result.Found > 0 ? "mixed" : null);
            result.IsComplete = target <= 0
                ? result.Found > 0
                : result.Found >= target;
            result.Succeeded = result.Found > 0;
            if (!result.IsComplete && result.Found > 0)
            {
                result.Warning =
                    target > 0
                        ? $"Device reports {target} enrolled user(s); portal pulled {result.Found}. Enable Push + User Data, server IP = portal PC (9920/9924), trigger Download/Sync on device, then fetch again."
                        : "List may be incomplete — verify count on device.";
            }

            if (!result.Succeeded)
            {
                result.Error =
                    target > 0
                        ? $"Could not read {target} user(s) from {endpoint.Host}. On device MENU enable Push + User Data (server = portal PC, not {endpoint.Host}), then retry."
                        : $"Could not read users from {endpoint.Host}. Enable Push + User Data on the device (portal listens on 9920).";
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PullAllEnrolled failed for {Host}", endpoint.Host);
            result.Error = ex.Message;
        }

        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    private async Task<int> ResolveEmployeeTargetCountAsync(
        HanwangTcpClient tcp,
        DeviceEndpoint endpoint,
        Dictionary<string, string> infoFields,
        CancellationToken cancellationToken)
    {
        var target = 0;
        foreach (var key in new[] { "real_employee", "employee", "alluser", "user_count", "usercount" })
        {
            if (int.TryParse(infoFields.GetValueOrDefault(key), out var n) && n > target)
                target = n;
        }

        foreach (var cmd in new[] { "GetEmployeeCount()", "GetEmployeeNum()" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await ExecuteOnSessionWithBusyRetryAsync(
                tcp, endpoint, cmd, TimeSpan.FromSeconds(12), cancellationToken);
            if (!raw.Contains("success", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = HanwangProtocolParser.ParseFields(raw.Replace('\n', ' '));
            foreach (var key in new[] { "count", "num", "number", "total", "employee", "real_employee" })
            {
                if (int.TryParse(fields.GetValueOrDefault(key), out var n) && n > target)
                    target = n;
            }
        }

        return target;
    }

    private async Task<string?> RunPushEmployeeDownloadAsync(
        HanwangTcpClient tcp,
        DeviceEndpoint endpoint,
        long deviceId,
        int target,
        int pushWaitSeconds,
        Dictionary<string, DeviceEmployeeRecord> merged,
        CancellationToken cancellationToken,
        bool extraTriggers = false)
    {
        var before = merged.Count;
        IReadOnlyList<DeviceEmployeeRecord> pushed;
        using (pushSync.BeginSession(deviceId, endpoint.Host, target))
        {
            var cmds = extraTriggers
                ? new[] { "ExportEmployee()", "DownloadEmployee()", "SyncEmployee()", "UploadEmployee()" }
                : new[] { "SyncEmployee()", "UploadEmployee()", "DownloadEmployee()" };
            foreach (var cmd in cmds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = await ExecuteOnSessionWithBusyRetryAsync(
                    tcp, endpoint, cmd, TimeSpan.FromSeconds(25), cancellationToken);
                await Task.Delay(600, cancellationToken);
            }

            var waitSec = extraTriggers ? Math.Max(pushWaitSeconds, 200) : pushWaitSeconds;
            var wait = TimeSpan.FromSeconds(Math.Clamp(waitSec, 30, 300));
            await pushSync.WaitForEmployeesAsync(deviceId, target, wait, cancellationToken);
            pushed = pushSync.TakeEmployees(deviceId);
        }

        foreach (var e in pushed)
        {
            var id = NicIdNormalizer.Normalize(e.EmployeeId);
            if (string.IsNullOrEmpty(id)) continue;
            e.EmployeeId = id;
            e.BioDeviceId = deviceId;
            merged[id] = e;
        }

        if (merged.Count <= before)
            return null;
        return extraTriggers ? "push-sync-2" : "push-sync";
    }

    private async Task TryPullViaAttendanceIdsAsync(
        DeviceEndpoint endpoint,
        Dictionary<string, DeviceEmployeeRecord> merged,
        int delayMs,
        CancellationToken cancellationToken,
        Action<string> noteMethod)
    {
        var idBag = new HashSet<string>(StringComparer.Ordinal);

        void AddIds(IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                var n = NicIdNormalizer.Normalize(id);
                if (!string.IsNullOrEmpty(n)) idBag.Add(n);
            }
        }

        var wide = await GetEmployeeIdsFromDeviceAttendanceAsync(endpoint, cancellationToken);
        AddIds(wide);

        var end = DateTime.Now;
        AddIds(await GetEmployeeIdsFromDeviceRecordsInRangeAsync(endpoint, end.AddDays(-90), end, cancellationToken));
        AddIds(await GetEmployeeIdsFromDeviceRecordsInRangeAsync(endpoint, end.AddYears(-1), end, cancellationToken));

        idBag.ExceptWith(merged.Keys);
        if (idBag.Count == 0)
            return;

        var pull = await PullEmployeesAsync(endpoint, idBag, delayMs, cancellationToken);
        foreach (var e in pull.Employees)
        {
            var id = NicIdNormalizer.Normalize(e.EmployeeId);
            if (string.IsNullOrEmpty(id)) continue;
            e.EmployeeId = id;
            e.BioDeviceId = endpoint.DeviceId;
            merged[id] = e;
        }

        if (pull.Employees.Count > 0)
            noteMethod("attendance-ids");
    }

    private async Task<List<DeviceEmployeeRecord>> TryPullBulkListAsync(
        HanwangTcpClient tcp,
        DeviceEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, DeviceEmployeeRecord>(StringComparer.Ordinal);

        void AbsorbRaw(string raw)
        {
            foreach (var row in HanwangProtocolParser.ParseReturnEmployeeRows(raw))
            {
                var emp = HanwangProtocolParser.ToDeviceEmployeeFromFields(row);
                if (emp is null) continue;
                var id = NicIdNormalizer.Normalize(emp.EmployeeId);
                if (string.IsNullOrEmpty(id)) continue;
                emp.EmployeeId = id;
                byId[id] = emp;
            }

            foreach (var row in HanwangProtocolParser.ParseReturn(raw).Rows)
            {
                if (row.ContainsKey("time")) continue;
                if (!row.ContainsKey("employee_id") && !row.ContainsKey("id")) continue;
                var emp = HanwangProtocolParser.ToDeviceEmployeeFromFields(row);
                if (emp is null) continue;
                var id = NicIdNormalizer.Normalize(emp.EmployeeId);
                if (string.IsNullOrEmpty(id)) continue;
                emp.EmployeeId = id;
                byId[id] = emp;
            }
        }

        foreach (var cmd in new[]
                 {
                     "GetUserList(start=\"0\" count=\"500\")",
                     "ExportEmployee()",
                     "GetEmployeePage(page=\"1\")",
                     "GetEmployeeByPage(page=\"1\" size=\"200\")",
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await ExecuteOnSessionWithBusyRetryAsync(
                tcp, endpoint, cmd, TimeSpan.FromSeconds(45), cancellationToken);
            if (!raw.Contains("success", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("Not Support", StringComparison.OrdinalIgnoreCase))
                continue;
            AbsorbRaw(raw);
            if (byId.Count > 0) break;
        }

        if (byId.Count == 0)
        {
            var pageSize = 200;
            for (var start = 0; start <= 20_000; start += pageSize)
            {
                var pageFound = 0;
                foreach (var template in new[]
                         {
                             "GetEmployeeList(start=\"{0}\" count=\"" + pageSize + "\")",
                             "GetAllEmployee(start=\"{0}\" count=\"" + pageSize + "\")",
                             "QueryEmployee(start=\"{0}\" count=\"" + pageSize + "\")",
                             "GetEmployeeInfo(start=\"{0}\" count=\"" + pageSize + "\")",
                         })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cmd = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, start);
                    var raw = await ExecuteOnSessionWithBusyRetryAsync(
                        tcp, endpoint, cmd, TimeSpan.FromSeconds(45), cancellationToken);
                    if (!raw.Contains("success", StringComparison.OrdinalIgnoreCase)
                        || raw.Contains("Not Support", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var before = byId.Count;
                    AbsorbRaw(raw);
                    pageFound = Math.Max(pageFound, byId.Count - before);
                    if (byId.Count > 0 && pageFound > 0) break;
                }

                if (pageFound == 0 && start > 0) break;
                if (pageFound < pageSize) break;
                if (byId.Count == 0 && start == 0 && pageFound == 0) break;
            }
        }

        return byId.Values.ToList();
    }

    private async Task<List<DeviceEmployeeRecord>> TryPullByIndexAsync(
        HanwangTcpClient tcp,
        DeviceEndpoint endpoint,
        int targetCount,
        int delayMs,
        bool exhaustive,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, DeviceEmployeeRecord>(StringComparer.Ordinal);
        var maxIndex = exhaustive
            ? Math.Min(Math.Max(targetCount + 150, 800), 10_000)
            : targetCount > 0 ? Math.Min(targetCount + 20, 2000) : 400;
        var missStreak = 0;
        var templates = new[]
        {
            "GetEmployeeByIndex(index=\"{0}\")",
            "GetEmployeeIndex(index=\"{0}\")",
            "GetUserByIndex(index=\"{0}\")",
            "GetEmployee(index=\"{0}\")",
        };

        for (var i = 0; i < maxIndex; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeviceEmployeeRecord? emp = null;
            string? hitRaw = null;
            foreach (var tpl in templates)
            {
                var cmd = string.Format(System.Globalization.CultureInfo.InvariantCulture, tpl, i);
                var raw = await ExecuteOnSessionWithBusyRetryAsync(
                    tcp, endpoint, cmd, TimeSpan.FromSeconds(30), cancellationToken);
                emp = HanwangProtocolParser.ToDeviceEmployee(raw, "");
                if (emp is not null)
                {
                    hitRaw = raw;
                    break;
                }
            }

            if (emp is null)
            {
                missStreak++;
                if (targetCount > 0 && found.Count >= targetCount) break;
                if (!exhaustive && found.Count > 0 && missStreak >= 25) break;
                if (!exhaustive && targetCount <= 0 && missStreak >= 40) break;
                continue;
            }

            missStreak = 0;
            var id = NicIdNormalizer.Normalize(emp.EmployeeId);
            if (!string.IsNullOrEmpty(id))
            {
                if (hitRaw is not null)
                    TryCachePhotoFromRaw(endpoint, id, hitRaw);
                found[id] = emp;
            }

            if (targetCount > 0 && found.Count >= targetCount) break;
            if (delayMs > 0)
            {
                var wait = exhaustive ? Math.Min(delayMs, 80) : delayMs;
                await Task.Delay(wait, cancellationToken);
            }
        }

        return found.Values.ToList();
    }

    public async Task<EmployeeDiffResult> ComparePortalAndDeviceAsync(
        DeviceEndpoint endpoint,
        IReadOnlyList<string> portalEmployeeIds,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new EmployeeDiffResult
        {
            DeviceId = endpoint.DeviceId,
        };

        var portal = portalEmployeeIds
            .Select(NicIdNormalizer.Normalize)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        result.PortalCount = portal.Count;

        var pull = await PullAllEnrolledAsync(endpoint, delayMsBetweenCalls, 90, cancellationToken);
        if (!pull.Succeeded && pull.Employees.Count == 0)
        {
            result.Succeeded = false;
            result.Error = pull.Error ?? "Could not read enrolled users from device.";
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        var deviceMap = pull.Employees.ToDictionary(
            e => NicIdNormalizer.Normalize(e.EmployeeId),
            e => e,
            StringComparer.Ordinal);
        result.DeviceCount = deviceMap.Count;
        result.Succeeded = true;
        result.ElapsedMs = sw.ElapsedMilliseconds;

        static EmployeeDiffItem ToItem(DeviceEmployeeRecord e) => new()
        {
            EmployeeId = e.EmployeeId,
            Name = e.Name,
            Department = e.Department,
            CheckType = e.CheckType,
            FaceTemplateCount = e.FaceTemplateCount,
            BiometricStatus = EmployeeBiometricStatus.FromRecord(e),
        };

        static EmployeeDiffItem PortalOnly(string id) => new()
        {
            EmployeeId = id,
            BiometricStatus = BiometricEnrollmentStatus.NoTemplate,
        };

        foreach (var id in portal.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (deviceMap.ContainsKey(id)) continue;
            result.OnlyOnPortal.Add(PortalOnly(id));
        }

        foreach (var kv in deviceMap.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (portal.Contains(kv.Key))
                result.OnBoth.Add(ToItem(kv.Value));
            else
                result.OnlyOnDevice.Add(ToItem(kv.Value));
        }

        result.MatchedCount = result.OnBoth.Count;
        return result;
    }

    public async Task<DeviceEmployeeUploadResult> UploadEmployeesAsync(
        DeviceEndpoint endpoint,
        Guid jobId,
        IReadOnlyList<DeviceEmployeeRecord> employees,
        int delayMsBetweenCalls = 150,
        int pushWaitSeconds = 120,
        CancellationToken cancellationToken = default,
        Action<DeviceEmployeeUploadProgress>? onProgress = null)
    {
        var sw = Stopwatch.StartNew();
        var lines = new List<EmployeeSyncLineResult>();
        var list = employees
            .Select(e =>
            {
                e.EmployeeId = NicIdNormalizer.Normalize(e.EmployeeId);
                return e;
            })
            .Where(e => !string.IsNullOrEmpty(e.EmployeeId))
            .ToList();

        var result = new DeviceEmployeeUploadResult { Requested = list.Count };
        if (list.Count == 0)
        {
            result.Succeeded = true;
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        var pushCommands = new List<string>();
        var hdcpFailed = new List<DeviceEmployeeRecord>();
        var preferPush = _defaults.EnablePushServer && _defaults.IsPushMode;
        var hdcpUnsupported = preferPush;

        try
        {
            if (preferPush)
            {
                foreach (var emp in list)
                {
                    lines.Add(new EmployeeSyncLineResult { EmployeeId = emp.EmployeeId, Name = emp.Name });
                    hdcpFailed.Add(emp);
                    pushCommands.Add(HanwangEmployeeUploadFormatter.BuildEmployeeCommand(emp));
                }
            }

            using (await DeviceConnectionLock.AcquireAsync(endpoint, cancellationToken))
            {
                using var tcp = new HanwangTcpClient(endpoint.Host, endpoint.Port);
                await tcp.ConnectAsync(TimeSpan.FromSeconds(15), cancellationToken);

                if (!preferPush)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var emp = list[i];
                        cancellationToken.ThrowIfCancellationRequested();
                        var line = new EmployeeSyncLineResult { EmployeeId = emp.EmployeeId, Name = emp.Name };
                        var ok = false;
                        string? lastErr = null;

                        if (!hdcpUnsupported)
                        {
                            foreach (var cmd in HanwangEmployeeUploadFormatter.BuildHdcpFallbackCommands(emp))
                            {
                                var raw = await ExecuteOnSessionWithBusyRetryAsync(
                                    tcp,
                                    endpoint,
                                    cmd,
                                    TimeSpan.FromSeconds(60),
                                    cancellationToken,
                                    maxAttempts: 2);
                                if (raw.Contains("success", StringComparison.OrdinalIgnoreCase)
                                    && !raw.Contains("fail", StringComparison.OrdinalIgnoreCase))
                                {
                                    ok = true;
                                    line.Method = "HDCP";
                                    break;
                                }

                                lastErr = raw.Length > 180 ? raw[..180] + "…" : raw;
                                if (IsHdcpUploadUnsupported(raw))
                                {
                                    hdcpUnsupported = true;
                                    break;
                                }
                            }
                        }

                        if (ok)
                        {
                            line.Succeeded = true;
                            lines.Add(line);
                        }
                        else
                        {
                            line.Succeeded = false;
                            line.Error = hdcpUnsupported
                                ? "HDCP upload not supported — using push"
                                : string.IsNullOrWhiteSpace(lastErr) ? "HDCP upload failed" : lastErr;
                            lines.Add(line);
                            hdcpFailed.Add(emp);
                            pushCommands.Add(HanwangEmployeeUploadFormatter.BuildEmployeeCommand(emp));
                        }

                        if (delayMsBetweenCalls > 0 && i < list.Count - 1)
                            await Task.Delay(delayMsBetweenCalls, cancellationToken);
                    }
                }
            }

            if (hdcpFailed.Count > 0 && _defaults.EnablePushServer)
            {
                await PushEmployeesViaDownloadServerAsync(
                    endpoint,
                    jobId,
                    hdcpFailed,
                    lines,
                    pushWaitSeconds,
                    hdcpUnsupported || preferPush,
                    delayMsBetweenCalls,
                    cancellationToken,
                    onProgress);
            }
            else if (hdcpFailed.Count > 0)
            {
                foreach (var emp in hdcpFailed)
                {
                    var line = lines.FirstOrDefault(l =>
                        string.Equals(NicIdNormalizer.Normalize(l.EmployeeId), emp.EmployeeId, StringComparison.Ordinal));
                    if (line is null || line.Succeeded) continue;
                    if (await TryDirectEmployeeHdcpUploadAsync(endpoint, emp, line, cancellationToken))
                        await ReconcileOneUploadLineAsync(endpoint, line, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UploadEmployees failed for {Host}", endpoint.Host);
            result.Error = ex.Message;
        }

        result.Lines = lines;
        result.Uploaded = lines.Count(l => l.Succeeded);
        result.Failed = lines.Count - result.Uploaded;
        result.Succeeded = result.Failed == 0;
        if (result.Uploaded > 0)
            result.Error = null;
        else if (result.Failed > 0)
        {
            var portalIp = string.IsNullOrWhiteSpace(_defaults.Host) ? "this portal PC" : _defaults.Host.Trim();
            result.Error =
                $"Could not apply {result.Failed} user(s) on {endpoint.Host}. On the target terminal: enable Push + User Data, " +
                $"set server IP to {portalIp}, ports {_defaults.CmdPushPort} & {_defaults.HbpPushPort}, " +
                "then run Network → Download/Sync employee and retry.";
        }

        foreach (var line in lines.Where(l => !l.Succeeded && string.IsNullOrWhiteSpace(l.Error)))
            line.Error = preferPush || hdcpUnsupported
                ? "Push download did not apply user on target"
                : "HDCP upload failed";

        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    private static bool IsHdcpUploadUnsupported(string raw) =>
        raw.Contains("Command Not Support", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("command not support", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// M2000-style terminals often complete only one user per DownloadEmployee session — push one at a time, then confirm on 9922.
    /// </summary>
    private async Task PushEmployeesViaDownloadServerAsync(
        DeviceEndpoint endpoint,
        Guid jobId,
        IReadOnlyList<DeviceEmployeeRecord> employees,
        List<EmployeeSyncLineResult> lines,
        int pushWaitSeconds,
        bool useFullPullTriggers,
        int delayMsBetweenCalls,
        CancellationToken cancellationToken,
        Action<DeviceEmployeeUploadProgress>? onProgress = null)
    {
        var hostKey = DevicePushSyncCoordinator.NormalizePeerIp(endpoint.Host);
        if (string.IsNullOrEmpty(hostKey))
            hostKey = endpoint.Host.Trim();

        var perEmployeeWaitSec = Math.Clamp(Math.Max(48, Math.Min(pushWaitSeconds, 90)), 48, 90);
        var total = employees.Count;

        for (var e = 0; e < employees.Count; e++)
        {
            var emp = employees[e];
            var line = lines.FirstOrDefault(l =>
                string.Equals(NicIdNormalizer.Normalize(l.EmployeeId), NicIdNormalizer.Normalize(emp.EmployeeId),
                    StringComparison.Ordinal));
            if (line is null || line.Succeeded)
                continue;

            ReportProgress(onProgress, e + 1, total, emp.EmployeeId, lines.Count(l => l.Succeeded), "Push to target");

            if (!useFullPullTriggers
                && await TryDirectEmployeeHdcpUploadAsync(endpoint, emp, line, cancellationToken))
            {
                await ReconcileOneUploadLineAsync(endpoint, line, cancellationToken);
                ReportProgress(onProgress, e + 1, total, emp.EmployeeId, lines.Count(l => l.Succeeded), "HDCP OK");
                if (line.Succeeded && delayMsBetweenCalls > 0 && e < employees.Count - 1)
                    await Task.Delay(delayMsBetweenCalls, cancellationToken);
                continue;
            }

            var pushed = false;
            for (var attempt = 1; attempt <= 2 && !pushed; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(onProgress, e + 1, total, emp.EmployeeId, lines.Count(l => l.Succeeded),
                    attempt > 1 ? "Push retry" : "Push + DownloadEmployee");
                pushed = await PushOneEmployeeViaPortalAsync(
                    hostKey,
                    endpoint,
                    jobId,
                    emp,
                    useFullPullTriggers,
                    perEmployeeWaitSec,
                    cancellationToken);
            }

            if (pushed)
            {
                line.Succeeded = true;
                line.Verified = true;
                line.Method = "Push download";
                line.Error = null;
            }
            else if (!useFullPullTriggers
                     && await TryDirectEmployeeHdcpUploadAsync(endpoint, emp, line, cancellationToken))
            {
                await ReconcileOneUploadLineAsync(endpoint, line, cancellationToken);
            }

            if (!line.Succeeded)
            {
                line.Error ??=
                    "Push/HDCP: user not on target — enable Push + User Data, correct server IP, then Sync on device.";
                ReportProgress(onProgress, e + 1, total, emp.EmployeeId, lines.Count(l => l.Succeeded), "Failed");
            }

            if (delayMsBetweenCalls > 0 && e < employees.Count - 1)
                await Task.Delay(Math.Min(delayMsBetweenCalls, 80), cancellationToken);
        }
    }

    private static void ReportProgress(
        Action<DeviceEmployeeUploadProgress>? onProgress,
        int index,
        int total,
        string employeeId,
        int uploadedSoFar,
        string phase)
    {
        onProgress?.Invoke(new DeviceEmployeeUploadProgress
        {
            Index = index,
            Total = total,
            EmployeeId = employeeId,
            UploadedSoFar = uploadedSoFar,
            Phase = phase,
        });
    }

    /// <summary>Poll GetEmployee while device downloads one queued Employee(...) from push port — same idea as Python push_listener + DownloadEmployee().</summary>
    private async Task<bool> PushOneEmployeeViaPortalAsync(
        string hostKey,
        DeviceEndpoint endpoint,
        Guid jobId,
        DeviceEmployeeRecord emp,
        bool useFullPullTriggers,
        int maxWaitSec,
        CancellationToken cancellationToken)
    {
        var cmd = HanwangEmployeeUploadFormatter.BuildEmployeeCommand(emp);
        using (downloadSync.BeginUpload(hostKey, jobId, [cmd]))
        {
            await TriggerPushDownloadsAsync(endpoint, useFullPullTriggers, cancellationToken, fastOnly: !useFullPullTriggers);
            var deadline = DateTime.UtcNow.AddSeconds(maxWaitSec);
            var lastTrig = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var onDevice = await FindEmployeeOnDeviceAsync(endpoint, emp.EmployeeId, cancellationToken);
                if (onDevice is not null)
                    return true;

                var progress = downloadSync.GetProgress(hostKey);
                if (progress?.Acknowledged >= 1)
                {
                    await Task.Delay(600, cancellationToken);
                    onDevice = await FindEmployeeOnDeviceAsync(endpoint, emp.EmployeeId, cancellationToken);
                    if (onDevice is not null)
                        return true;
                }

                if (DateTime.UtcNow - lastTrig >= TimeSpan.FromSeconds(8))
                {
                    _ = await ExecuteWithRetryAsync(
                        endpoint, "DownloadEmployee()", TimeSpan.FromSeconds(18), cancellationToken, maxAttempts: 1);
                    lastTrig = DateTime.UtcNow;
                }

                await Task.Delay(1200, cancellationToken);
            }
        }

        return await FindEmployeeOnDeviceAsync(endpoint, emp.EmployeeId, cancellationToken) is not null;
    }

    private async Task<bool> TryDirectEmployeeHdcpUploadAsync(
        DeviceEndpoint endpoint,
        DeviceEmployeeRecord emp,
        EmployeeSyncLineResult line,
        CancellationToken cancellationToken)
    {
        try
        {
            using (await DeviceConnectionLock.AcquireAsync(endpoint, cancellationToken, TimeSpan.FromSeconds(120)))
            {
                using var tcp = new HanwangTcpClient(endpoint.Host, endpoint.Port);
                await tcp.ConnectAsync(TimeSpan.FromSeconds(15), cancellationToken);
                foreach (var cmd in HanwangEmployeeUploadFormatter.BuildHdcpFallbackCommands(emp))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var raw = await ExecuteOnSessionWithBusyRetryAsync(
                        tcp, endpoint, cmd, TimeSpan.FromSeconds(90), cancellationToken, maxAttempts: 2);
                    if (raw.Contains("success", StringComparison.OrdinalIgnoreCase)
                        && !raw.Contains("fail", StringComparison.OrdinalIgnoreCase)
                        && !IsHdcpUploadUnsupported(raw))
                    {
                        line.Succeeded = true;
                        line.Method = "HDCP Employee";
                        line.Error = null;
                        return true;
                    }

                    if (IsHdcpUploadUnsupported(raw))
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Direct HDCP Employee upload failed for {Id} on {Host}", emp.EmployeeId, endpoint.Host);
        }

        return false;
    }

    private async Task ReconcileOneUploadLineAsync(
        DeviceEndpoint endpoint,
        EmployeeSyncLineResult line,
        CancellationToken cancellationToken)
    {
        var onDevice = await FindEmployeeOnDeviceWithRetriesAsync(
            endpoint, line.EmployeeId, attempts: 3, delayMs: 600, cancellationToken);
        if (onDevice is null) return;
        line.Succeeded = true;
        line.Verified = true;
        line.Error = null;
    }

    private async Task<(bool Acked, bool Sent)> WaitForPushDownloadAsync(
        string hostKey,
        DeviceEndpoint endpoint,
        bool useFullPullTriggers,
        int minAck,
        TimeSpan maxWait,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(maxWait);
        var lastRetrigger = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = downloadSync.GetProgress(hostKey);
            if (progress is not null)
            {
                if (progress.Acknowledged >= minAck)
                    return (true, progress.Sent >= 1);
                if (progress.Sent >= minAck)
                {
                    await Task.Delay(1200, cancellationToken);
                    if (downloadSync.GetProgress(hostKey)?.Acknowledged >= minAck)
                        return (true, true);
                }
            }

            if (DateTime.UtcNow - lastRetrigger >= TimeSpan.FromSeconds(14))
            {
                await TriggerPushDownloadsAsync(endpoint, useFullPullTriggers, cancellationToken, fastOnly: false);
                lastRetrigger = DateTime.UtcNow;
            }

            await Task.Delay(320, cancellationToken);
        }

        var fin = downloadSync.GetProgress(hostKey);
        return (fin?.Acknowledged >= minAck, fin?.Sent >= 1);
    }

    private async Task TriggerPushDownloadsAsync(
        DeviceEndpoint endpoint,
        bool useFullPullTriggers,
        CancellationToken cancellationToken,
        bool fastOnly = false)
    {
        if (fastOnly)
        {
            _ = await ExecuteWithRetryAsync(
                endpoint, "DownloadEmployee()", TimeSpan.FromSeconds(20), cancellationToken, maxAttempts: 2);
            await Task.Delay(280, cancellationToken);
            return;
        }

        var triggers = useFullPullTriggers
            ? new[] { "ExportEmployee()", "DownloadEmployee()", "SyncEmployee()", "UploadEmployee()" }
            : new[] { "DownloadEmployee()", "SyncEmployee()", "UploadEmployee()" };
        foreach (var trigger in triggers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await ExecuteWithRetryAsync(endpoint, trigger, TimeSpan.FromSeconds(22), cancellationToken, maxAttempts: 2);
            await Task.Delay(320, cancellationToken);
        }
    }

    private async Task<bool> WaitForPushAckAsync(
        string hostKey,
        int minAck,
        TimeSpan maxWait,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(maxWait);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = downloadSync.GetProgress(hostKey);
            if (progress is not null && progress.Acknowledged >= minAck)
                return true;
            await Task.Delay(280, cancellationToken);
        }

        return downloadSync.GetProgress(hostKey)?.Acknowledged >= minAck;
    }

    private async Task<DeviceEmployeeRecord?> FindEmployeeOnDeviceAsync(
        DeviceEndpoint endpoint,
        string employeeId,
        CancellationToken cancellationToken)
    {
        var id = NicIdNormalizer.Normalize(employeeId);
        if (string.IsNullOrEmpty(id)) return null;

        foreach (var cmd in new[]
                 {
                     $"GetEmployee(employee_id=\"{id}\")",
                     $"GetEmployee(id=\"{id}\")",
                     $"GetEmployee(employee_id=\"{id}\" photo=\"0\")",
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await ExecuteWithRetryAsync(endpoint, cmd, TimeSpan.FromSeconds(45), cancellationToken);
            if (raw.Contains("unknown id", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("not exist", StringComparison.OrdinalIgnoreCase))
                continue;
            var emp = HanwangProtocolParser.ToDeviceEmployee(raw, id);
            if (emp is not null)
            {
                emp.EmployeeId = NicIdNormalizer.Normalize(emp.EmployeeId);
                return emp;
            }
        }

        return null;
    }

    private async Task<DeviceEmployeeRecord?> FindEmployeeOnDeviceWithRetriesAsync(
        DeviceEndpoint endpoint,
        string employeeId,
        int attempts,
        int delayMs,
        CancellationToken cancellationToken)
    {
        for (var a = 1; a <= attempts; a++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var emp = await FindEmployeeOnDeviceAsync(endpoint, employeeId, cancellationToken);
            if (emp is not null) return emp;
            if (a < attempts && delayMs > 0)
                await Task.Delay(delayMs, cancellationToken);
        }

        return null;
    }

    private async Task ReconcileUploadLinesOnDeviceAsync(
        DeviceEndpoint endpoint,
        List<EmployeeSyncLineResult> lines,
        int delayMsBetweenCalls,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Succeeded) continue;

            cancellationToken.ThrowIfCancellationRequested();
            var onDevice = await FindEmployeeOnDeviceWithRetriesAsync(
                endpoint, line.EmployeeId, attempts: 2, delayMs: 1200, cancellationToken);
            if (onDevice is null) continue;

            line.Succeeded = true;
            line.Method = string.IsNullOrEmpty(line.Method) ? "On device" : line.Method + " (verified)";
            line.Error = null;
            if (delayMsBetweenCalls > 0 && i < lines.Count - 1)
                await Task.Delay(delayMsBetweenCalls, cancellationToken);
        }
    }

    public async Task VerifyEmployeesOnDeviceAsync(
        DeviceEndpoint endpoint,
        IList<EmployeeSyncLineResult> lines,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            cancellationToken.ThrowIfCancellationRequested();
            var emp = await FindEmployeeOnDeviceWithRetriesAsync(
                endpoint, line.EmployeeId, attempts: 3, delayMs: 1200, cancellationToken);
            if (emp is not null)
            {
                if (!line.Succeeded)
                {
                    line.Succeeded = true;
                    line.Method = string.IsNullOrEmpty(line.Method) ? "Verified on device" : line.Method;
                    line.Error = null;
                }

                line.Verified = true;
            }
            else
            {
                line.Verified = false;
                if (line.Succeeded && string.IsNullOrEmpty(line.Error))
                    line.Error = "GetEmployee: not found after upload";
            }

            if (delayMsBetweenCalls > 0 && i < lines.Count - 1)
                await Task.Delay(delayMsBetweenCalls, cancellationToken);
        }
    }

    public async Task<EmployeeDeleteResult> DeleteEmployeesAsync(
        DeviceEndpoint endpoint,
        IEnumerable<string> employeeIds,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var ids = employeeIds
            .Select(NicIdNormalizer.Normalize)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var lines = new List<EmployeeSyncLineResult>();
        foreach (var id in ids)
        {
            var line = new EmployeeSyncLineResult { EmployeeId = id };
            var ok = false;
            string? last = null;
            foreach (var cmd in new[]
                     {
                         $"DeleteEmployee(employee_id=\"{id}\")",
                         $"RemoveEmployee(employee_id=\"{id}\")",
                         $"DelEmployee(employee_id=\"{id}\")",
                         $"DeleteEmployee(id=\"{id}\")",
                     })
            {
                var raw = await ExecuteWithRetryAsync(endpoint, cmd, TimeSpan.FromSeconds(45), cancellationToken, 2);
                last = raw.Length > 160 ? raw[..160] + "…" : raw;
                if (raw.Contains("success", StringComparison.OrdinalIgnoreCase)
                    && !raw.Contains("unknown id", StringComparison.OrdinalIgnoreCase)
                    && !raw.Contains("fail", StringComparison.OrdinalIgnoreCase))
                {
                    ok = true;
                    line.Method = "HDCP";
                    break;
                }
            }

            line.Succeeded = ok;
            if (!ok) line.Error = string.IsNullOrWhiteSpace(last) ? "Delete failed" : last;
            lines.Add(line);
            if (delayMsBetweenCalls > 0) await Task.Delay(delayMsBetweenCalls, cancellationToken);
        }

        return new EmployeeDeleteResult
        {
            Succeeded = lines.All(l => l.Succeeded),
            Requested = ids.Count,
            Deleted = lines.Count(l => l.Succeeded),
            Failed = lines.Count(l => !l.Succeeded),
            Lines = lines,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private async Task<string> ExecuteOnSessionWithBusyRetryAsync(
        HanwangTcpClient tcp,
        DeviceEndpoint endpoint,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int maxAttempts = 3)
    {
        string last = "";
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            last = await tcp.ExecuteOnSessionAsync(command, timeout, cancellationToken);
            if (!last.Contains("Device Is Busy", StringComparison.Ordinal))
                return last;

            logger.LogDebug("Device busy on {Host} during {Command}, retry {Attempt}/{Max}",
                endpoint.Host, command, attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return last;
    }

    private async Task<string> ExecuteWithRetryAsync(
        DeviceEndpoint endpoint,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int maxAttempts = 2)
    {
        using (await DeviceConnectionLock.AcquireAsync(endpoint, cancellationToken, TimeSpan.FromSeconds(90)))
        {
            string last = "";
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var tcp = new HanwangTcpClient(endpoint.Host, endpoint.Port);
                last = await tcp.ExecuteAsync(command, timeout, cancellationToken);
                if (!last.Contains("Device Is Busy", StringComparison.Ordinal))
                    return last;

                logger.LogDebug("Device busy on {Host}, retry {Attempt}/{Max}", endpoint.Host, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(maxAttempts > 4 ? 2.5 : 2), cancellationToken);
            }

            return last;
        }
    }

    public DeviceEndpoint ToEndpoint(BiometricDevice device) => new()
    {
        DeviceId = device.DeviceId,
        Host = device.DeviceIp,
        Port = device.DevicePort,
        RecordStart = _defaults.RecordStart,
        RecordEnd = _defaults.RecordEnd,
    };

    public async Task EnrichDisplayPhotosAsync(
        DeviceEndpoint endpoint,
        IEnumerable<DeviceEmployeeRecord> employees,
        int delayMsBetweenCalls = 120,
        CancellationToken cancellationToken = default)
    {
        if (endpoint.DeviceId <= 0) return;

        var list = employees
            .Select(e => NicIdNormalizer.Normalize(e.EmployeeId))
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < list.Count; i++)
        {
            var id = list[i];
            cancellationToken.ThrowIfCancellationRequested();
            if (photoCache.TryGet(endpoint.DeviceId, id, out var bytes, out _) && bytes is { Length: > 0 })
                continue;

            _ = await LookupEmployeeWithPhotoAsync(endpoint, id, cancellationToken);
            if (delayMsBetweenCalls > 0 && i < list.Count - 1)
                await Task.Delay(delayMsBetweenCalls, cancellationToken);
        }
    }

    private void TryCachePhotoFromRaw(DeviceEndpoint endpoint, string employeeId, string raw)
    {
        if (endpoint.DeviceId <= 0 || string.IsNullOrWhiteSpace(raw)) return;
        var id = NicIdNormalizer.Normalize(employeeId);
        if (string.IsNullOrEmpty(id)) return;
        if (photoCache.TryGet(endpoint.DeviceId, id, out var existing, out _) && existing is { Length: > 0 })
            return;

        var decoded = HanwangEmployeePhotoDecoder.TryExtractFromEmployeeResponse(raw);
        if (decoded is not null)
            photoCache.Store(endpoint.DeviceId, id, decoded.Bytes, decoded.ContentType);
    }
}
