using System.Collections.Concurrent;
using BiometricDashboard.Core.Interfaces;

namespace BiometricDashboard.Infrastructure.Services;

public sealed class EmployeePhotoCache : IEmployeePhotoCache
{
    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

    public void Store(long deviceId, string employeeId, byte[] imageBytes, string contentType)
    {
        if (deviceId <= 0 || string.IsNullOrWhiteSpace(employeeId) || imageBytes.Length == 0) return;
        var key = Key(deviceId, employeeId);
        _cache[key] = new Cached(imageBytes, contentType);
    }

    public bool TryGet(long deviceId, string employeeId, out byte[]? imageBytes, out string? contentType)
    {
        imageBytes = null;
        contentType = null;
        if (deviceId <= 0 || string.IsNullOrWhiteSpace(employeeId)) return false;
        if (!_cache.TryGetValue(Key(deviceId, employeeId), out var hit)) return false;
        imageBytes = hit.Bytes;
        contentType = hit.ContentType;
        return true;
    }

    private static string Key(long deviceId, string employeeId) =>
        deviceId + ":" + employeeId.Trim();

    private sealed record Cached(byte[] Bytes, string ContentType);
}