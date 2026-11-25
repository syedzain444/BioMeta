using BiometricDashboard.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace BiometricDashboard.Web.ViewComponents;

public sealed class PortalNavViewComponent(ISecurityRepository security, ICurrentUserContext user) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!user.IsAuthenticated || user.UserId is null)
            return View(Array.Empty<NavItem>());

        var modules = await security.ListModulesForNavigationAsync(user.UserId.Value, HttpContext.RequestAborted);
        var items = modules.Select(m => new NavItem
        {
            Name = m.ModuleName,
            RoutePath = m.RoutePath ?? "/",
            IconClass = DockIcon(m.ModuleCode, m.IconClass),
            ModuleCode = m.ModuleCode,
            DockColorClass = DockColorClass(m.ModuleCode),
            DockLabel = DockLabel(m.ModuleCode, m.ModuleName),
            IsActive = IsActive(m.RoutePath),
        }).ToList();
        return View(items);
    }

    private bool IsActive(string? route)
    {
        if (string.IsNullOrEmpty(route)) return false;
        var path = HttpContext.Request.Path.Value ?? "";
        return path.StartsWith(route, StringComparison.OrdinalIgnoreCase);
    }

    private static string DockIcon(string moduleCode, string? fallback) => moduleCode.ToLowerInvariant() switch
    {
        "dashboard" => "bi-house-door",
        "logs" => "bi-clipboard2-pulse",
        "devices" => "bi-display",
        "users" => "bi-people",
        "device_users" => "bi-person-vcard",
        "device_photos" => "bi-camera",
        "device_enroll_hub" => "bi-layers",
        "device_enroll_sync" => "bi-arrow-left-right",
        "roles" => "bi-shield-lock",
        _ => fallback ?? "bi-circle",
    };

    private static string DockColorClass(string moduleCode) => moduleCode.ToLowerInvariant() switch
    {
        "dashboard" => "dock-c-teal",
        "logs" => "dock-c-purple",
        "devices" => "dock-c-orange",
        "users" => "dock-c-cyan",
        "device_users" => "dock-c-indigo",
        "device_photos" => "dock-c-cyan",
        "device_enroll_hub" => "dock-c-indigo",
        "device_enroll_sync" => "dock-c-teal",
        "roles" => "dock-c-pink",
        _ => "dock-c-slate",
    };

    private static string DockLabel(string moduleCode, string moduleName) => moduleCode.ToLowerInvariant() switch
    {
        "dashboard" => "Dashboard",
        "logs" => "Attendance Logs",
        "devices" => "Devices",
        "users" => "User Management",
        "device_users" => "Device Users",
        "device_photos" => "Employee Photos",
        "device_enroll_hub" => "Enrollment hub",
        "device_enroll_sync" => "Enrollment sync",
        "roles" => "Role Management",
        _ => moduleName,
    };
}

public sealed class NavItem
{
    public string Name { get; set; } = "";
    public string RoutePath { get; set; } = "";
    public string IconClass { get; set; } = "";
    public string ModuleCode { get; set; } = "";
    public string DockColorClass { get; set; } = "dock-c-slate";
    public string DockLabel { get; set; } = "";
    public bool IsActive { get; set; }
}
