using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Utilities;

public static class DeviceConnectivity
{
    public static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(30);

    public static bool IsRecentlyOnline(DeviceSnapshot? snapshot, DateTime? lastSeenAtUtc)
    {
        if (snapshot is not null)
        {
            var checkedUtc = snapshot.CheckedAt.Kind == DateTimeKind.Utc
                ? snapshot.CheckedAt
                : snapshot.CheckedAt.ToUniversalTime();
            if (checkedUtc >= DateTime.UtcNow.Subtract(LiveWindow))
                return snapshot.IsOnline;
        }

        if (lastSeenAtUtc is { } seen)
        {
            var utc = seen.Kind == DateTimeKind.Utc ? seen : seen.ToUniversalTime();
            if (utc >= DateTime.UtcNow.Subtract(LiveWindow))
                return true;
        }

        return false;
    }
}
