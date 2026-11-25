using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Core.Options;
using BiometricDashboard.Infrastructure.Device;
using BiometricDashboard.Infrastructure.Persistence;
using BiometricDashboard.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var config = new ConfigurationBuilder()
    .SetBasePath(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BiometricDashboard.Web")))
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var factory = new OracleConnectionFactory(config);
var devices = new DeviceRepository(factory);
var repo = new OracleAttendanceRepository(factory, devices, NullLogger<OracleAttendanceRepository>.Instance);
var client = new HanwangDeviceClient(Microsoft.Extensions.Options.Options.Create(new DeviceOptions()), NullLogger<HanwangDeviceClient>.Instance);

var endpoint = new DeviceEndpoint
{
    DeviceId = 1,
    Host = "172.16.40.139",
    Port = 9922,
    RecordStart = "2020-01-01 00:00:00",
    RecordEnd = "2030-12-31 23:59:59",
};

var pull = await client.PullAttendanceAsync(endpoint);
Console.WriteLine($"Fetched {pull.Records.Count} from device");
var added = await repo.UpsertManyAsync(pull.Records);
Console.WriteLine($"Inserted {added.Count} new rows");

Console.WriteLine($"GetRecent device 1: {(await repo.GetRecentAsync(10, 1)).Count}");
Console.WriteLine($"Count device 1: {await repo.CountAsync(1)}");
