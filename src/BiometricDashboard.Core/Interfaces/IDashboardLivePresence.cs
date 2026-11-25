namespace BiometricDashboard.Core.Interfaces;

/// <summary>True when at least one browser is connected to the live attendance SignalR hub.</summary>
public interface IDashboardLivePresence
{
    bool AnyViewer { get; }
}
