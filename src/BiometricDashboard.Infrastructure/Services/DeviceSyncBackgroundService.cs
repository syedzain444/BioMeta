using System.Collections.Concurrent;
using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Infrastructure.Device;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BiometricDashboard.Infrastructure.Services;

public sealed class DeviceSyncBackgroundService : BackgroundService, IDeviceSyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeviceOptions _options;
    private readonly ILogger<DeviceSyncBackgroundService> _logger;
    private readonly ConcurrentDictionary<long, DeviceSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<long, DateTime> _lastInfoUtc = new();
    private readonly ConcurrentDictionary<long, int> _lastBufferCount = new();

    public DeviceSnapshot? LatestSnapshot =>
        _snapshots.Values.OrderByDescending(s => s.CheckedAt).FirstOrDefault();

    public IReadOnlyDictionary<long, DeviceSnapshot> SnapshotsByDeviceId => _snapshots;

    public DeviceSnapshot? GetSnapshot(long deviceId) =>
        _snapshots.TryGetValue(deviceId, out var s) ? s : null;

    public event Func<IReadOnlyList<AttendanceRecord>, Task>? NewRecordsReceived;

    public DeviceSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<DeviceOptions> options,
        ILogger<DeviceSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public Task NotifyPushedRecordsAsync(IReadOnlyList<AttendanceRecord> records, CancellationToken cancellationToken = default) =>
        NotifyAddedAsync(records);

    public void MarkPushHeartbeat(long bioDeviceId, string? deviceSerial = null)
    {
        var snap = _snapshots.GetOrAdd(bioDeviceId, _ => new DeviceSnapshot());
        snap.IsOnline = true;
        snap.CheckedAt = DateTime.UtcNow;
        snap.LastError = null;
        if (!string.IsNullOrWhiteSpace(deviceSerial))
            snap.SerialNumber = deviceSerial;
        snap.RecordCountOnDevice = Math.Max(0, snap.RecordCountOnDevice) + 1;
        _snapshots[bioDeviceId] = snap;

        _ = PersistPushHeartbeatAsync(bioDeviceId, deviceSerial);
    }

    private async Task PersistPushHeartbeatAsync(long bioDeviceId, string? deviceSerial)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            await deviceRepo.UpdateSyncStatusAsync(bioDeviceId, deviceSerial, online: true, error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist push heartbeat for device {DeviceId}", bioDeviceId);
        }
    }

    public async Task RecordProbeFailureAsync(long bioDeviceId, string error, CancellationToken cancellationToken = default)
    {
        var snap = new DeviceSnapshot
        {
            CheckedAt = DateTime.UtcNow,
            IsOnline = false,
            LastError = error,
        };
        _snapshots[bioDeviceId] = snap;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            await deviceRepo.UpdateSyncStatusAsync(bioDeviceId, serial: null, online: false, error, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist probe failure for device {DeviceId}", bioDeviceId);
        }
    }

    public Task<DeviceConnectionTestResult> ProbeEndpointAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var h = host.Trim();
        var p = port > 0 ? port : 9922;
        var built = BuildProbeEndpoint(0, h, p);
        return RunProbeAsync(built.Endpoint, built.WindowStart, built.WindowEnd, persistDeviceId: null, cancellationToken);
    }

    public async Task<DeviceConnectionTestResult> ProbeDeviceAsync(long bioDeviceId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var device = await deviceRepo.GetByIdAsync(bioDeviceId, cancellationToken);
        if (device is null)
            return new DeviceConnectionTestResult { Message = "Device not found." };

        var host = device.DeviceIp.Trim();
        var port = device.DevicePort > 0 ? device.DevicePort : 9922;
        var built = BuildProbeEndpoint(device.DeviceId, host, port);
        return await RunProbeAsync(built.Endpoint, built.WindowStart, built.WindowEnd, persistDeviceId: bioDeviceId, cancellationToken);
    }

    private (DeviceEndpoint Endpoint, string WindowStart, string WindowEnd) BuildProbeEndpoint(long deviceId, string host, int port)
    {
        // Device clocks and GetRecord filters use local wall time — not UTC.
        var probeEnd = DateTime.Now;
        var probeStart = probeEnd.Date.AddDays(-6);
        var fmt = "yyyy-MM-dd HH:mm:ss";
        var windowStart = probeStart.ToString(fmt);
        var windowEnd = probeEnd.ToString(fmt);
        var endpoint = new DeviceEndpoint
        {
            DeviceId = deviceId,
            Host = host,
            Port = port,
            RecordStart = windowStart,
            RecordEnd = windowEnd,
        };
        return (endpoint, windowStart, windowEnd);
    }

    private async Task<DeviceConnectionTestResult> RunProbeAsync(
        DeviceEndpoint endpoint,
        string probeWindowStart,
        string probeWindowEnd,
        long? persistDeviceId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var client = scope.ServiceProvider.GetRequiredService<IDeviceClient>();
        var attendance = scope.ServiceProvider.GetRequiredService<IAttendanceRepository>();

        var host = endpoint.Host;
        var port = endpoint.Port;

        var tcp = await DevicePortProbe.TryConnectAsync(
            host,
            port,
            TimeSpan.FromSeconds(8),
            attempts: 3,
            cancellationToken);

        const int probeAttempts = 8;
        DeviceSnapshot snap;
        if (!tcp.Reachable)
        {
            snap = new DeviceSnapshot
            {
                CheckedAt = DateTime.UtcNow,
                IsOnline = false,
                LastError = tcp.Error,
            };
        }
        else
        {
            snap = await client.GetDeviceSnapshotAsync(endpoint, cancellationToken, probeAttempts);
            snap.CheckedAt = DateTime.UtcNow;
        }

        var dataOk = false;
        var sampleCount = 0;
        int? totalOnDevice = null;
        int? portalRows = null;
        string? message = null;

        if (!tcp.Reachable)
        {
            message =
                "Ping may work, but the portal could not open TCP " +
                $"{host}:{port}. {tcp.Error}";
        }
        else if (snap.IsOnline)
        {
            var pull = await client.PullAttendanceAsync(endpoint, cancellationToken, probeAttempts);
            dataOk = pull.Succeeded;
            sampleCount = pull.Records.Count;
            totalOnDevice = pull.RecordTotalOnDevice;

            if (pull.Succeeded && sampleCount == 0
                && (snap.RecordCountOnDevice > 0 || totalOnDevice is > 0))
            {
                var wide = new DeviceEndpoint
                {
                    DeviceId = endpoint.DeviceId,
                    Host = host,
                    Port = port,
                    RecordStart = _options.RecordStart,
                    RecordEnd = _options.RecordEnd,
                };
                var widePull = await client.PullAttendanceAsync(wide, cancellationToken, probeAttempts);
                if (widePull.Records.Count > 0)
                {
                    sampleCount = widePull.Records.Count;
                    totalOnDevice = widePull.RecordTotalOnDevice;
                }
            }

            if (persistDeviceId is long devId && devId > 0)
            {
                try
                {
                    portalRows = await attendance.CountAsync(devId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Probe portal attendance count skipped for device {Id}", devId);
                }
            }

            if (!pull.Succeeded)
            {
                message = "GetDeviceInfo succeeded but GetRecord failed — check date range and device logs.";
                snap.LastError ??= message;
            }
            else if (sampleCount == 0 && totalOnDevice is > 0)
            {
                message =
                    $"Device reports {totalOnDevice} record(s) but none in probe window ({probeWindowStart} → {probeWindowEnd}).";
            }
            else if (sampleCount > 0)
            {
                message =
                    $"Device responded; {sampleCount} record(s) returned (probe {probeWindowStart} → {probeWindowEnd}).";
            }
            else if (_options.IsPushMode)
            {
                var buf = snap.RecordCountOnDevice;
                message =
                    $"HDCP OK. Push mode: terminal buffer is often empty after upload (GetDeviceInfo buffer: {buf}).";
                if (portalRows is > 0)
                    message += $" Portal DB has {portalRows:N0} attendance row(s) for this device — live punches should appear on Dashboard/Logs.";
                else
                    message +=
                        " No attendance in portal DB yet — on device set Push server IP to this portal PC (not the terminal IP), ports 9920/9924 open, then scan once.";
            }
            else
            {
                message =
                    $"Device responded; no attendance in probe window ({probeWindowStart} → {probeWindowEnd}).";
                if (portalRows is > 0)
                    message += $" Portal DB has {portalRows:N0} row(s) for this device.";
            }
        }
        else
        {
            message = snap.LastError ?? "Device did not respond to GetDeviceInfo.";
        }

        if (persistDeviceId is long id && id > 0)
        {
            _snapshots[id] = snap;
            _lastInfoUtc[id] = DateTime.UtcNow;
            if (snap.IsOnline)
                _lastBufferCount[id] = snap.RecordCountOnDevice;

            try
            {
                await deviceRepo.UpdateSyncStatusAsync(
                    id,
                    snap.SerialNumber,
                    snap.IsOnline,
                    snap.IsOnline ? null : snap.LastError,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // Edit-page TestDraft does not persist; list Test/{id} does. A DB/permission error here must not
                // hide a successful HDCP probe — return probe JSON and log the persist failure.
                _logger.LogWarning(
                    ex,
                    "Probe OK but failed to persist sync status for device {DeviceId} ({Host}:{Port})",
                    id,
                    host,
                    port);
            }
        }

        return new DeviceConnectionTestResult
        {
            Host = host,
            Port = port,
            TcpReachable = tcp.Reachable,
            TcpLatencyMs = tcp.LatencyMs,
            TcpError = tcp.Error,
            Online = snap.IsOnline,
            DataChannelOk = snap.IsOnline && dataOk,
            SampleRecordCount = sampleCount,
            TotalRecordsOnDevice = totalOnDevice,
            PortalAttendanceRows = portalRows,
            ProbeWindowStart = probeWindowStart,
            ProbeWindowEnd = probeWindowEnd,
            Snapshot = snap,
            Message = message,
        };
    }

    public async Task<DevicePullSyncResult> PullOnceAsync(
        long bioDeviceId,
        bool refreshDeviceInfo = false,
        CancellationToken cancellationToken = default)
    {
        if (_options.IsPushMode)
            return new DevicePullSyncResult { Snapshot = GetSnapshot(bioDeviceId) };

        using var scope = _scopeFactory.CreateScope();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var client = scope.ServiceProvider.GetRequiredService<IDeviceClient>();
        var repo = scope.ServiceProvider.GetRequiredService<IAttendanceRepository>();

        var device = await deviceRepo.GetByIdAsync(bioDeviceId, cancellationToken);
        if (device is null)
            return new DevicePullSyncResult { Snapshot = GetSnapshot(bioDeviceId) };

        var endpoint = new DeviceEndpoint
        {
            DeviceId = device.DeviceId,
            Host = device.DeviceIp,
            Port = device.DevicePort,
            RecordStart = _options.RecordStart,
            RecordEnd = _options.RecordEnd,
        };

        var infoDue = refreshDeviceInfo
                        || DateTime.UtcNow - _lastInfoUtc.GetValueOrDefault(bioDeviceId, DateTime.MinValue)
                        >= TimeSpan.FromSeconds(Math.Max(30, _options.DeviceInfoIntervalSeconds));

        if (refreshDeviceInfo || (!_snapshots.ContainsKey(bioDeviceId) && infoDue))
        {
            var snap = await client.GetDeviceSnapshotAsync(endpoint, cancellationToken);
            snap.CheckedAt = DateTime.UtcNow;
            _snapshots[bioDeviceId] = snap;
            _lastInfoUtc[bioDeviceId] = DateTime.UtcNow;
            _lastBufferCount[bioDeviceId] = snap.RecordCountOnDevice;

            await deviceRepo.UpdateSyncStatusAsync(
                bioDeviceId,
                snap.SerialNumber,
                snap.IsOnline,
                snap.LastError,
                cancellationToken);

            return new DevicePullSyncResult { Snapshot = snap };
        }

        if (!_snapshots.TryGetValue(bioDeviceId, out var cached) || !cached.IsOnline)
            return new DevicePullSyncResult { Snapshot = cached };

        var added = await PullAndPersistAsync(client, repo, device, endpoint, cached, cancellationToken);
        if (added.Count > 0)
            await NotifyAddedAsync(added);

        return new DevicePullSyncResult
        {
            Added = added,
            Snapshot = _snapshots.GetValueOrDefault(bioDeviceId),
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var onDemand = _options.IsPushMode
                       || !string.Equals(_options.SyncMode, "Background", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation(
            "Device sync service started ({Mode})",
            _options.IsPushMode ? "Push — no 9922 poll" :
            onDemand ? "OnDemand — 9922 only when dashboard syncs" : "Background polling");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (onDemand)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            await RunBackgroundLoopAsync(stoppingToken);
        }
    }

    private async Task RunBackgroundLoopAsync(CancellationToken stoppingToken)
    {
        var loopDelaySeconds = Math.Max(_options.PollIntervalSeconds, _options.IdlePollIntervalSeconds);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var devices = await deviceRepo.ListDevicesAsync(activeOnly: true, stoppingToken);
            foreach (var device in devices)
                await PullOnceAsync(device.DeviceId, refreshDeviceInfo: false, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background sync loop error");
        }

        await Task.Delay(TimeSpan.FromSeconds(loopDelaySeconds), stoppingToken);
    }

    private async Task NotifyAddedAsync(IReadOnlyList<AttendanceRecord> added)
    {
        _logger.LogInformation("New punches synced: {Count}", added.Count);
        var handler = NewRecordsReceived;
        if (handler != null)
            await handler.Invoke(added);
    }

    private async Task<List<AttendanceRecord>> PullAndPersistAsync(
        IDeviceClient client,
        IAttendanceRepository repo,
        BiometricDevice device,
        DeviceEndpoint endpoint,
        DeviceSnapshot snap,
        CancellationToken stoppingToken)
    {
        var pull = await client.PullAttendanceAsync(endpoint, stoppingToken);
        if (pull.RecordTotalOnDevice is int total)
        {
            snap.RecordCountOnDevice = total;
            snap.CheckedAt = DateTime.UtcNow;
            _snapshots[device.DeviceId] = snap;
            _lastBufferCount[device.DeviceId] = total;
        }

        var serial = pull.DeviceSerial ?? snap.SerialNumber ?? device.DeviceSerial ?? "";
        foreach (var rec in pull.Records)
        {
            if (string.IsNullOrWhiteSpace(rec.DeviceSerial))
                rec.DeviceSerial = serial;
            rec.DeviceName = device.DeviceName;
            rec.DeviceLocation = device.DeviceLocation ?? "";
        }

        var added = (await repo.UpsertManyAsync(pull.Records, stoppingToken)).ToList();
        foreach (var rec in added)
        {
            rec.DeviceName = device.DeviceName;
            rec.DeviceLocation = device.DeviceLocation ?? "";
        }

        return added;
    }
}
