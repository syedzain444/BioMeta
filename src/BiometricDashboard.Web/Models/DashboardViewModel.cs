using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Web.Models;

public class DashboardViewModel
{
    public DeviceSnapshot? Device { get; set; }
    public IReadOnlyList<AttendanceRecord> RecentRecords { get; set; } = Array.Empty<AttendanceRecord>();
    public int TotalSynced { get; set; }
    public string DeviceHost { get; set; } = "";
}
