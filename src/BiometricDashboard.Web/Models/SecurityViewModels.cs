using System.ComponentModel.DataAnnotations;

namespace BiometricDashboard.Web.Models;

public sealed class LoginViewModel
{
    [Required, Display(Name = "Employee ID")]
    public string EmployeeId { get; set; } = "";

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";

    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public sealed class UserFormViewModel
{
    public long? UserId { get; set; }

    [Required, Display(Name = "Employee ID")]
    public string EmployeeId { get; set; } = "";

    [Required, Display(Name = "Display name")]
    public string DisplayName { get; set; } = "";

    [EmailAddress]
    public string? Email { get; set; }

    [DataType(DataType.Password), Display(Name = "Password")]
    public string? Password { get; set; }

    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public List<long> SelectedRoleIds { get; set; } = [];
    public IReadOnlyList<RoleOption> Roles { get; set; } = [];
}

public sealed class RoleOption
{
    public long RoleId { get; set; }
    public string RoleName { get; set; } = "";
}

public sealed class RoleFormViewModel
{
    public long? RoleId { get; set; }

    [Required]
    public string RoleCode { get; set; } = "";

    [Required]
    public string RoleName { get; set; } = "";

    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public List<long> SelectedPermissionIds { get; set; } = [];
    public IReadOnlyList<PermissionGroupViewModel> PermissionGroups { get; set; } = [];
}

public sealed class PermissionGroupViewModel
{
    public string ModuleName { get; set; } = "";
    public List<PermissionOption> Permissions { get; set; } = [];
}

public sealed class PermissionOption
{
    public long PermissionId { get; set; }
    public string PermissionName { get; set; } = "";
    public string PermissionCode { get; set; } = "";
}

public sealed class UserListViewModel
{
    public IReadOnlyList<UserRowViewModel> Users { get; set; } = [];
}

public sealed class UserRowViewModel
{
    public long UserId { get; set; }
    public string EmployeeId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public bool IsActive { get; set; }
    public IReadOnlyList<string> RoleCodes { get; set; } = [];
}

public sealed class RoleListViewModel
{
    public IReadOnlyList<RoleRowViewModel> Roles { get; set; } = [];
}

public sealed class RoleRowViewModel
{
    public long RoleId { get; set; }
    public string RoleCode { get; set; } = "";
    public string RoleName { get; set; } = "";
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; }
}
