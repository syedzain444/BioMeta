using System.Collections.Concurrent;
using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Infrastructure.Device;

/// <summary>One HDCP session per terminal (host:port) — different devices can talk in parallel.</summary>
internal static class DeviceConnectionLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(DeviceEndpoint endpoint) =>
        $"{endpoint.Host.Trim()}:{endpoint.Port}";

    public static async Task<IDisposable> AcquireAsync(
        DeviceEndpoint endpoint,
        CancellationToken cancellationToken,
        TimeSpan? maxWait = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Host))
            throw new ArgumentException("Device host is required.", nameof(endpoint));

        var key = Key(endpoint);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        if (maxWait is { } wait)
        {
            if (!await gate.WaitAsync(wait, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Terminal {key} is busy (enrollment fetch, sync, or another test on this IP). Wait or use Cancel on Enrollment hub, then retry.");
            }
        }
        else
        {
            await gate.WaitAsync(cancellationToken);
        }

        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}