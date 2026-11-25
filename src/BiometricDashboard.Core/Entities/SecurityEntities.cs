namespace BiometricDashboard.Core.Entities;

public sealed class SecUser : AuditableEntity
{
    public long UserId { get; set; }
    public string EmployeeId { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public IReadOnlyList<string> RoleCodes { get; set; } = [];
    public IReadOnlyList<string> Permissions { get; set; } = [];
}

public sealed class SecRole : AuditableEntity
{
    public long RoleId { get; set; }
    public string RoleCode { get; set; } = "";
    public string RoleName { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SecModule : AuditableEntity
{
    public long ModuleId { get; set; }
    public string ModuleCode { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string? RoutePath { get; set; }
    public string? IconClass { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SecPermission : AuditableEntity
{
    public long PermissionId { get; set; }
    public long ModuleId { get; set; }
    public string PermissionCode { get; set; } = "";
    public string PermissionName { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public string? ModuleName { get; set; }
}
