using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Web.Authorization;
using BiometricDashboard.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace BiometricDashboard.Web.Controllers;

[Route("Admin/Users")]
[RequirePermission("users.view")]
public sealed class AdminUsersController(
    ISecurityRepository security,
    IAuthService auth,
    ICurrentUserContext currentUser) : Controller
{
    [HttpGet("")]
    [HttpGet("Index")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var users = await security.ListUsersAsync(cancellationToken);
        var vm = new UserListViewModel
        {
            Users = users.Select(u => new UserRowViewModel
            {
                UserId = u.UserId,
                EmployeeId = u.EmployeeId,
                DisplayName = u.DisplayName,
                Email = u.Email,
                IsActive = u.IsActive,
                RoleCodes = u.RoleCodes,
            }).ToList(),
        };
        return View(vm);
    }

    [RequirePermission("users.manage")]
    [HttpGet("Create")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken) =>
        View("Edit", await BuildFormAsync(null, cancellationToken));

    [RequirePermission("users.manage")]
    [HttpGet("Edit/{id:long}")]
    public async Task<IActionResult> Edit(long id, CancellationToken cancellationToken)
    {
        var user = await security.GetUserByIdAsync(id, cancellationToken);
        if (user is null) return NotFound();
        return View(await BuildFormAsync(user, cancellationToken));
    }

    [RequirePermission("users.manage")]
    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UserFormViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            model.Roles = await LoadRoleOptionsAsync(cancellationToken);
            return View("Edit", model);
        }

        var actor = currentUser.UserId ?? 1;
        if (model.UserId is null or 0)
        {
            if (string.IsNullOrWhiteSpace(model.Password))
            {
                ModelState.AddModelError(nameof(model.Password), "Password is required for new users.");
                model.Roles = await LoadRoleOptionsAsync(cancellationToken);
                return View("Edit", model);
            }

            var user = new SecUser
            {
                EmployeeId = model.EmployeeId.Trim(),
                DisplayName = model.DisplayName.Trim(),
                Email = model.Email,
                PasswordHash = auth.HashPassword(model.Password),
                IsActive = model.IsActive,
                MustChangePassword = model.MustChangePassword,
            };
            await security.CreateUserAsync(user, model.SelectedRoleIds, actor, cancellationToken);
        }
        else
        {
            var user = await security.GetUserByIdAsync(model.UserId.Value, cancellationToken);
            if (user is null) return NotFound();
            user.DisplayName = model.DisplayName.Trim();
            user.Email = model.Email;
            user.IsActive = model.IsActive;
            user.MustChangePassword = model.MustChangePassword;
            if (!string.IsNullOrWhiteSpace(model.Password))
                user.PasswordHash = auth.HashPassword(model.Password);
            await security.UpdateUserAsync(user, model.SelectedRoleIds, actor, cancellationToken);
        }

        TempData["Success"] = "User saved.";
        return RedirectToAction(nameof(Index));
    }

    [RequirePermission("users.manage")]
    [HttpPost("Delete/{id:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        if (currentUser.UserId == id)
        {
            TempData["Error"] = "You cannot delete your own account.";
            return RedirectToAction(nameof(Index));
        }
        await security.SoftDeleteUserAsync(id, currentUser.UserId ?? 1, cancellationToken);
        TempData["Success"] = "User removed.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<UserFormViewModel> BuildFormAsync(SecUser? user, CancellationToken cancellationToken)
    {
        var roleIds = user is not null
            ? await security.GetRoleIdsForUserAsync(user.UserId, cancellationToken)
            : [];
        return new UserFormViewModel
        {
            UserId = user?.UserId,
            EmployeeId = user?.EmployeeId ?? "",
            DisplayName = user?.DisplayName ?? "",
            Email = user?.Email,
            IsActive = user?.IsActive ?? true,
            MustChangePassword = user?.MustChangePassword ?? false,
            SelectedRoleIds = roleIds.ToList(),
            Roles = await LoadRoleOptionsAsync(cancellationToken),
        };
    }

    private async Task<IReadOnlyList<RoleOption>> LoadRoleOptionsAsync(CancellationToken cancellationToken)
    {
        var roles = await security.ListRolesAsync(cancellationToken);
        return roles.Where(r => r.IsActive).Select(r => new RoleOption { RoleId = r.RoleId, RoleName = r.RoleName }).ToList();
    }
}
