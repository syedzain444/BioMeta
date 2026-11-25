using System.IO.Compression;
using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Core.Utilities;
using Microsoft.Extensions.Options;

namespace BiometricDashboard.Web.Services;

public sealed class EmployeePhotoBulkZipService(
    IDeviceRepository devices,
    IDeviceClient deviceClient,
    IEmployeePhotoCache photoCache,
    IOptions<DeviceOptions> deviceOptions)
{
    private readonly DeviceOptions _opts = deviceOptions.Value;

    public sealed class Item
    {
        public long DeviceId { get; set; }
        public string EmployeeId { get; set; } = "";
        public string? Name { get; set; }
    }

    public sealed class Result
    {
        public bool Succeeded { get; set; }
        public string? Error { get; set; }
        public byte[]? ZipBytes { get; set; }
        public int Added { get; set; }
    }

    public async Task<Result> BuildZipAsync(IReadOnlyList<Item> items, CancellationToken cancellationToken)
    {
        var result = new Result();
        if (items.Count == 0)
        {
            result.Error = "No employees selected.";
            return result;
        }

        var deviceMap = (await devices.ListDevicesAsync(activeOnly: true, cancellationToken))
            .ToDictionary(d => d.DeviceId);

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items.Take(500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = NicIdNormalizer.Normalize(item.EmployeeId);
                if (string.IsNullOrEmpty(id) || item.DeviceId <= 0) continue;

                if (!photoCache.TryGet(item.DeviceId, id, out var bytes, out var contentType) || bytes is null or { Length: 0 })
                {
                    if (!deviceMap.TryGetValue(item.DeviceId, out var dev)) continue;
                    var lookup = await deviceClient.LookupEmployeeWithPhotoAsync(ToEndpoint(dev), id, cancellationToken);
                    if (!lookup.HasDisplayPhoto) continue;
                    if (!photoCache.TryGet(item.DeviceId, id, out bytes, out contentType) || bytes is null or { Length: 0 })
                        continue;
                }

                var baseName = EmployeePhotoFileNames.Build(item.Name, id, contentType);
                var entryName = baseName;
                var suffix = 2;
                while (!usedNames.Add(entryName))
                    entryName = Path.GetFileNameWithoutExtension(baseName) + "_" + suffix++ + Path.GetExtension(baseName);

                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(bytes, cancellationToken);
                result.Added++;
            }
        }

        if (result.Added == 0)
        {
            result.Error = "No display photos available — fetch from device first (大头照 on terminal).";
            return result;
        }

        zipStream.Position = 0;
        result.ZipBytes = zipStream.ToArray();
        result.Succeeded = true;
        return result;
    }

    private DeviceEndpoint ToEndpoint(BiometricDevice d) => new()
    {
        DeviceId = d.DeviceId,
        Host = d.DeviceIp,
        Port = d.DevicePort,
        RecordStart = _opts.RecordStart,
        RecordEnd = _opts.RecordEnd,
    };
}