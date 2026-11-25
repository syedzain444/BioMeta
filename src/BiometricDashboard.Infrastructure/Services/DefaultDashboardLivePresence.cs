using BiometricDashboard.Core.Interfaces;

namespace BiometricDashboard.Infrastructure.Services;

/// <summary>When no web UI — prefer slow idle polling (do not hammer 9922).</summary>
internal sealed class DefaultDashboardLivePresence : IDashboardLivePresence
{
    public bool AnyViewer => false;
}
