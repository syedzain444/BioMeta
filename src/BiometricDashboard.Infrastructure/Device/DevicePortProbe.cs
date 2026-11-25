using System.Diagnostics;
using System.Net.Sockets;

namespace BiometricDashboard.Infrastructure.Device;

/// <summary>
/// ICMP ping only proves routing; Hanwang terminals speak HDCP on a TCP port (usually 9922).
/// </summary>
public static class DevicePortProbe
{
    public sealed record Result(bool Reachable, long LatencyMs, string? Error);

    public static async Task<Result> TryConnectAsync(
        string host,
        int port,
        TimeSpan connectTimeout,
        int attempts = 3,
        CancellationToken cancellationToken = default)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(connectTimeout);
                await client.ConnectAsync(host, port, cts.Token);
                sw.Stop();
                return new Result(true, sw.ElapsedMilliseconds, null);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                sw.Stop();
                last = ex;
                if (attempt < attempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken);
            }
        }

        var msg = last switch
        {
            OperationCanceledException => "TCP connect timed out.",
            SocketException se => DescribeSocket(se, host, port),
            _ => last?.Message ?? "TCP connect failed.",
        };
        return new Result(false, 0, msg);
    }

    private static string DescribeSocket(SocketException se, string host, int port)
    {
        return se.SocketErrorCode switch
        {
            SocketError.TimedOut =>
                $"TCP to {host}:{port} timed out. Ping can succeed while this port is closed, filtered, or the device service is down.",
            SocketError.ConnectionRefused =>
                $"TCP {host}:{port} refused — nothing listening (wrong port or device HDCP off).",
            SocketError.HostUnreachable =>
                $"Host {host} unreachable from the portal server (not from your PC). Run the portal where sync runs.",
            SocketError.NetworkUnreachable => $"No route to {host} from the portal server.",
            _ => $"TCP {host}:{port} failed: {se.Message}",
        };
    }
}
