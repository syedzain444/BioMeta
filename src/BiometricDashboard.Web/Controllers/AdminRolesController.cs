using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Web.Authorization;
using BiometricDashboard.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace BiometricDashboard.Web.Controllers;

[Route("Admin/Roles")]
[RequirePermission("roles.view")]
public sealed class AdminRolesController(ISecurityRepository security, ICurrentUserContext currentUser) : Controller
{
    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var roles = await security.ListRolesAsync(cancellationToken);
        return View(new RoleListViewModel
        {
            Roles = roles.Select(r => new RoleRowViewModel
            {
                RoleId = r.RoleId,
                RoleCode = r.RoleCode,
                RoleName = r.RoleName,
                IsSystem = r.IsSystem,
                IsActive = r.IsActive,
            }).ToList(),
        });
    }

    [RequirePermission("roles.manage")]
    [HttpGet("Create")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken) =>
        View("Edit", await BuildFormAsync(null, cancellationToken));

    [RequirePermission("roles.manage")]
    [HttpGet("Edit/{id:long}")]
    public async Task<IActionResult> Edit(long id, CancellationToken cancellationToken)
    {
        var roles = await security.ListRolesAsync(cancellationToken);
        var role = roles.FirstOrDefault(r => r.RoleId == id);
        if (role is null) return NotFound();
        return View(await BuildFormAsync(role, cancellationToken));
    }

    [RequirePermission("roles.manage")]
    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(RoleFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            model.PermissionGroups = await LoadPermissionGroupsAsync(cancellationToken);
            return View("Edit", model);
        }

        var actor = currentUser.UserId ?? 1;
        if (model.RoleId is null or 0)
        {
            var role = new SecRole
            {
                RoleCode = model.RoleCode.Trim().ToUpperInvariant(),
                RoleName = model.RoleName.Trim(),
                Description = model.Description,
                IsActive = model.IsActive,
            };
            await security.CreateRoleAsync(role, model.SelectedPermissionIds, actor, cancellationToken);
        }
        else
        {
            var roles = await security.ListRolesAsync(cancellationToken);
            var role = roles.FirstOrDefault(r => r.RoleId == model.RoleId);
            if (role is null) return NotFound();
            if (role.IsSystem)
                model.RoleCode = role.RoleCode;
            role.RoleName = model.RoleName.Trim();
            role.Description = model.Description;
            role.IsActive = model.IsActive;
            await security.UpdateRoleAsync(role, model.SelectedPermissionIds, actor, cancellationToken);
        }

        TempData["Success"] = "Role saved.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<RoleFormViewModel> BuildFormAsync(SecRole? role, CancellationToken cancellationToken)
    {
        var permIds = role is not null
            ? await security.GetPermissionIdsForRoleAsync(role.RoleId, cancellationToken)
            : [];
        return new RoleFormViewModel
        {
            RoleId = role?.RoleId,
            RoleCode = role?.RoleCode ?? "",
            RoleName = role?.RoleName ?? "",
            Description = role?.Description,
            IsActive = role?.IsActive ?? true,
            SelectedPermissionIds = permIds.ToList(),
            PermissionGroups = await LoadPermissionGroupsAsync(cancellationToken),
        };
    }

    private async Task<IReadOnlyList<PermissionGroupViewModel>> LoadPermissionGroupsAsync(CancellationToken cancellationToken)
    {
        var perms = await security.ListPermissionsAsync(cancellationToken);
        return perms
            .GroupBy(p => p.ModuleName ?? "General")
            .Select(g => new PermissionGroupViewModel
            {
                ModuleName = g.Key,
                Permissions = g.Select(p => new PermissionOption
                {
                    PermissionId = p.PermissionId,
                    PermissionName = p.PermissionName,
                    PermissionCode = p.PermissionCode,
                }).ToList(),
            })
            .ToList();
    }
}
