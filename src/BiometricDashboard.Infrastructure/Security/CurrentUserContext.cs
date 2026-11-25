using System.Security.Claims;
using BiometricDashboard.Core.Interfaces;
using Microsoft.AspNetCore.Http;

namespace BiometricDashboard.Infrastructure.Security;

public sealed class CurrentUserContext(IHttpContextAccessor accessor) : ICurrentUserContext
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    public long? UserId => long.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? EmployeeId => User?.FindFirstValue("employee_id");

    public string? DisplayName => User?.FindFirstValue(ClaimTypes.Name);

    public IReadOnlySet<string> Permissions =>
        User?.FindAll("permission").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool HasPermission(string permissionCode) =>
        Permissions.Contains(permissionCode, StringComparer.OrdinalIgnoreCase);
}
