using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;

namespace BiometricDashboard.Web.Services;

public sealed class SelectedDeviceService(IHttpContextAccessor http, IDeviceRepository devices)
{
    private const string SessionKey = "SelectedBioDeviceId";

    public long? GetSelectedDeviceId()
    {
        var id = http.HttpContext?.Session.GetInt32(SessionKey);
        return id is > 0 ? id : null;
    }

    public void SetSelectedDeviceId(long deviceId) =>
        http.HttpContext?.Session.SetInt32(SessionKey, (int)deviceId);

    public async Task<BiometricDevice?> GetSelectedDeviceAsync(CancellationToken cancellationToken = default)
    {
        var id = GetSelectedDeviceId();
        if (id is not null)
        {
            var d = await devices.GetByIdAsync(id.Value, cancellationToken);
            if (d is not null && d.IsActive) return d;
        }

        var primary = await devices.GetPrimaryAsync(cancellationToken);
        if (primary is not null)
            SetSelectedDeviceId(primary.DeviceId);
        return primary;
    }
}
