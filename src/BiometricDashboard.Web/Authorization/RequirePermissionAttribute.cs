using Microsoft.AspNetCore.Authorization;

namespace BiometricDashboard.Web.Authorization;

public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public RequirePermissionAttribute(string permission) => Policy = permission;
}
