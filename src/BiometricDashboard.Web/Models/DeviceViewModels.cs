using System.ComponentModel.DataAnnotations;

namespace BiometricDashboard.Web.Models;

public sealed class DeviceFormViewModel
{
    public long? DeviceId { get; set; }

    [Required, Display(Name = "Terminal IP")]
    public string DeviceIp { get; set; } = "172.16.40.63";

    /// <summary>TCP port on the terminal for HDCP pull (GetDeviceInfo / Test). Not CMD/HBP push ports.</summary>
    [Display(Name = "HDCP port (on terminal)")]
    public int DevicePort { get; set; } = 9922;

    [Required, Display(Name = "Device admin ID")]
    public string AdminId { get; set; } = "";

    [DataType(DataType.Password), Display(Name = "Device admin password")]
    public string? AdminPassword { get; set; }

    [Required, Display(Name = "Device name / alias")]
    public string DeviceName { get; set; } = "";

    [Required, Display(Name = "Location")]
    public string DeviceLocation { get; set; } = "";

    public bool IsActive { get; set; } = true;
    public bool IsPrimary { get; set; }
    public bool IsInitialSetup { get; set; }

    /// <summary>Set by AJAX test before save (optional device serial from GetDeviceInfo).</summary>
    public string? LastTestSerial { get; set; }
}

public sealed class DeviceListViewModel
{
    public IReadOnlyList<DeviceRowViewModel> Devices { get; set; } = [];
    public PortalServerPortsViewModel ServerPorts { get; set; } = new();
}

/// <summary>Portal listen ports (configure matching values on each terminal).</summary>
public sealed class PortalServerPortsViewModel
{
    public string SyncMode { get; set; } = "Push";
    public bool PushServerEnabled { get; set; }
    public string BindAddress { get; set; } = "0.0.0.0";
    public int CmdPort { get; set; } = 9920;
    public int HbpPort { get; set; } = 9924;
    public int HdcpPort { get; set; } = 9922;
}

public sealed class DeviceRowViewModel
{
    public long DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public string DeviceIp { get; set; } = "";
    public int DevicePort { get; set; }
    public string? DeviceLocation { get; set; }
    public string? DeviceSerial { get; set; }
    public bool IsActive { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastSyncError { get; set; }
    /// <summary>Recent GetDeviceInfo / push heartbeat / last seen.</summary>
    public bool IsOnline { get; set; }
}

public sealed class DeviceSwitcherViewModel
{
    public long? SelectedDeviceId { get; set; }
    public IReadOnlyList<DeviceOptionViewModel> Devices { get; set; } = [];
}

public sealed class DeviceOptionViewModel
{
    public long DeviceId { get; set; }
    public string Label { get; set; } = "";
}

public sealed class DeviceMenuViewModel
{
    public long? SelectedDeviceId { get; set; }
    public string DeviceName { get; set; } = "No device";
    public string DeviceIp { get; set; } = "";
    public bool IsLive { get; set; }
    public IReadOnlyList<DeviceMenuItemViewModel> Devices { get; set; } = [];
}

public sealed class DeviceMenuItemViewModel
{
    public long DeviceId { get; set; }
    public string DeviceName { get; set; } = "";
    public string DeviceIp { get; set; } = "";
    public bool IsLive { get; set; }
    public bool IsSelected { get; set; }
}
