using System.Net;
using System.Net.Sockets;
using System.Text;
using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BiometricDashboard.Infrastructure.Device;

/// <summary>
/// Hanwang push server (9920/9924). Device pushes <c>Record(...)</c> on scan — no GetRecord pull on GetRequest
/// (that was causing stuck “Network Operating” on the terminal).
/// </summary>
public sealed class HanwangPushServerHostedService : BackgroundService
{
    private static readonly Encoding PushEncoding = CreatePushEncoding();

    private static Encoding CreatePushEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GBK");
    }

    private readonly DeviceOptions _options;
    private readonly DeviceSyncBackgroundService _sync;
    private readonly DevicePushSyncCoordinator _pushEmployees;
    private readonly DeviceEmployeeDownloadCoordinator _downloadEmployees;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HanwangPushServerHostedService> _logger;

    public HanwangPushServerHostedService(
        IOptions<DeviceOptions> options,
        DeviceSyncBackgroundService sync,
        DevicePushSyncCoordinator pushEmployees,
        DeviceEmployeeDownloadCoordinator downloadEmployees,
        IServiceScopeFactory scopeFactory,
        ILogger<HanwangPushServerHostedService> logger)
    {
        _options = options.Value;
        _sync = sync;
        _pushEmployees = pushEmployees;
        _downloadEmployees = downloadEmployees;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnablePushServer || !_options.IsPushMode)
        {
            _logger.LogInformation("Hanwang push server disabled (EnablePushServer or SyncMode != Push)");
            return;
        }

        var listeners = new List<TcpListener>();
        foreach (var port in _options.PushPorts.Distinct())
        {
            var bindAddr = _options.PushBindAddress is "0.0.0.0" or "*"
                ? IPAddress.Any
                : IPAddress.Parse(_options.PushBindAddress);
            var listener = new TcpListener(bindAddr, port);
            try
            {
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
            }
            catch (SocketException ex)
            {
                foreach (var l in listeners)
                    l.Stop();
                _logger.LogError(
                    ex,
                    "Push port {Port} already in use. Stop python push_listener.exe, end extra BiometricDashboard.Web processes, then restart.",
                    port);
                return;
            }

            listeners.Add(listener);
            _logger.LogInformation("Push server listening on {Bind}:{Port}", _options.PushBindAddress, port);
            _ = AcceptLoopAsync(listener, port, stoppingToken);
        }

        if (listeners.Count == 0)
            return;

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            foreach (var l in listeners)
                l.Stop();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, int port, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                client.Client.LingerState = new LingerOption(false, 0);
                _ = HandleClientAsync(client, port, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Push accept error on port {Port}", port);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, int port, CancellationToken stoppingToken)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var peerIp = DevicePushSyncCoordinator.NormalizePeerIp(remote?.Address.ToString());
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var msg = await ReadOneMessageAsync(stream, stoppingToken);
                    if (string.IsNullOrEmpty(msg)) break;
                    if (msg.StartsWith("Quit", StringComparison.Ordinal)) break;

                    if (msg.StartsWith("Record(", StringComparison.Ordinal))
                    {
                        await HandleRecordAsync(msg, peerIp, stoppingToken);
                        await SendAsync(stream, """Return(result="success")""");
                        continue;
                    }

                    if (msg.StartsWith("PostRecord", StringComparison.Ordinal))
                    {
                        var photo = "false";
                        await SendAsync(stream, $"""Return(result="success" postphoto="{photo}")""");
                        continue;
                    }

                    if (msg.StartsWith("PostEmployee", StringComparison.Ordinal))
                    {
                        await SendAsync(stream, """Return(result="success")""");
                        continue;
                    }

                    if (msg.StartsWith("Return(", StringComparison.Ordinal))
                    {
                        if (_downloadEmployees.GetProgress(peerIp) is not null)
                            _downloadEmployees.Acknowledge(peerIp);
                        continue;
                    }

                    if (msg.StartsWith("Employee(", StringComparison.Ordinal))
                    {
                        HandleEmployeePush(msg, peerIp);
                        await SendAsync(stream, """Return(result="success")""");
                        continue;
                    }

                    if (msg.StartsWith("GetRequest", StringComparison.Ordinal))
                    {
                        if (_downloadEmployees.TryTakeNextCommand(peerIp, out var nextCmd, out _))
                        {
                            await SendAsync(stream, nextCmd);
                        }
                        else
                        {
                            // Do NOT send GetRecord — device stays idle, no “Network Operating” loop.
                            await SendAsync(stream, """Return(result="success")""");
                        }
                        continue;
                    }

                    await SendAsync(stream, """Return(result="success")""");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Push client {Ip}:{Port} ended", peerIp, port);
            }
        }
    }

    private void HandleEmployeePush(string msg, string peerIp)
    {
        var emp = HanwangProtocolParser.ToDeviceEmployee(msg, "");
        if (emp is null) return;
        _pushEmployees.TryAddEmployee(peerIp, emp);
    }

    private async Task HandleRecordAsync(string msg, string peerIp, CancellationToken stoppingToken)
    {
        var inner = msg.AsSpan(7, msg.Length - 8).ToString();
        var fields = HanwangProtocolParser.ParseFields(inner.Replace('\n', ' '));
        if (!fields.ContainsKey("time") || !fields.ContainsKey("id"))
            return;

        using var scope = _scopeFactory.CreateScope();
        var deviceRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
        var devices = await deviceRepo.ListDevicesAsync(activeOnly: false, stoppingToken);
        var device = devices.FirstOrDefault(d =>
            string.Equals(DevicePushSyncCoordinator.NormalizePeerIp(d.DeviceIp), peerIp, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            _logger.LogWarning("Push Record from unknown IP {Ip} — add device in portal", peerIp);
            return;
        }

        var serial = device.DeviceSerial ?? "";
        var rec = HanwangProtocolParser.ToAttendance(fields, serial);
        if (rec is null) return;

        rec.BioDeviceId = device.DeviceId;
        rec.DeviceName = device.DeviceName;
        rec.DeviceLocation = device.DeviceLocation ?? "";
        rec.CardSource = string.IsNullOrEmpty(rec.CardSource) ? "push" : rec.CardSource;

        _sync.MarkPushHeartbeat(device.DeviceId, serial);

        var repo = scope.ServiceProvider.GetRequiredService<IAttendanceRepository>();
        var added = await repo.UpsertManyAsync(new[] { rec }, stoppingToken);
        if (added.Count > 0)
            await _sync.NotifyPushedRecordsAsync(added, stoppingToken);
    }

    private static async Task SendAsync(NetworkStream stream, string text)
    {
        if (!text.EndsWith("\r\n", StringComparison.Ordinal))
            text += "\r\n";
        var bytes = PushEncoding.GetBytes(text);
        await stream.WriteAsync(bytes);
    }

    private static async Task<string?> ReadOneMessageAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            var depth = 0;
            var started = false;
            for (var i = 0; i < sb.Length; i++)
            {
                var ch = sb[i];
                if (ch == '(')
                {
                    depth++;
                    started = true;
                }
                else if (ch == ')')
                {
                    depth--;
                    if (started && depth == 0)
                        return sb.ToString(0, i + 1);
                }
            }

            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return sb.Length > 0 ? sb.ToString() : null;
            sb.Append(PushEncoding.GetString(buffer, 0, read));
        }
    }
}
