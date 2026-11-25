namespace BiometricDashboard.Web.Models;

public sealed class EmployeePhotoBulkDownloadRequest
{
    public List<EmployeePhotoBulkItem>? Items { get; set; }
}

public sealed class EmployeePhotoBulkItem
{
    public long DeviceId { get; set; }
    public string EmployeeId { get; set; } = "";
    public string? Name { get; set; }
}