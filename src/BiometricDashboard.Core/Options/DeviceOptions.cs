namespace BiometricDashboard.Core.Options;

public class DeviceOptions
{
    public const string SectionName = "BiometricDevice";

    /// <summary>Push = device sends Record to 9920/9924 (no 9922 poll). OnDemand = dashboard pull. Background = legacy loop.</summary>
    public string SyncMode { get; set; } = "Push";

    public bool EnablePushServer { get; set; } = true;

    public string PushBindAddress { get; set; } = "0.0.0.0";

    /// <summary>Device menu: CMD port (push commands / records).</summary>
    public int CmdPushPort { get; set; } = 9920;

    /// <summary>Device menu: HBP port (heartbeat / bulk push).</summary>
    public int HbpPushPort { get; set; } = 9924;

    public int[] PushPorts { get; set; } = [9920, 9924];

    public string Host { get; set; } = "172.16.40.63";
    public int Port { get; set; } = 9922;

    public int DashboardPollSeconds { get; set; } = 3;
    public int PollIntervalSeconds { get; set; } = 6;
    public int IdlePollIntervalSeconds { get; set; } = 60;
    public int DeviceInfoIntervalSeconds { get; set; } = 45;
    public int IdleDeviceInfoIntervalSeconds { get; set; } = 300;

    public string RecordStart { get; set; } = "2020-01-01 00:00:00";
    public string RecordEnd { get; set; } = "2030-12-31 23:59:59";

    public bool IsPushMode =>
        string.Equals(SyncMode, "Push", StringComparison.OrdinalIgnoreCase);
}
