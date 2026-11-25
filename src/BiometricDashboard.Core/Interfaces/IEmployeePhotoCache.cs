namespace BiometricDashboard.Core.Interfaces;

public interface IEmployeePhotoCache
{
    void Store(long deviceId, string employeeId, byte[] imageBytes, string contentType);
    bool TryGet(long deviceId, string employeeId, out byte[]? imageBytes, out string? contentType);
}