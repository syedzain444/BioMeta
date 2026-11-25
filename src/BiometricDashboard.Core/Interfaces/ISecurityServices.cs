using BiometricDashboard.Core.Entities;

namespace BiometricDashboard.Core.Interfaces;

public interface ICurrentUserContext
{
    long? UserId { get; }
    string? EmployeeId { get; }
    string? DisplayName { get; }
    bool IsAuthenticated { get; }
    IReadOnlySet<string> Permissions { get; }
    bool HasPermission(string permissionCode);
}

public interface IAuthService
{
    Task<SecUser?> ValidateLoginAsync(string employeeId, string password, CancellationToken cancellationToken = default);
    Task RecordLastLoginAsync(long userId, CancellationToken cancellationToken = default);
    string HashPassword(string plainPassword);
    bool VerifyPassword(string plainPassword, string hash);
}

public interface ISecurityRepository
{
    Task<SecUser?> GetUserByEmployeeIdAsync(string employeeId, CancellationToken cancellationToken = default);
    Task<SecUser?> GetUserByIdAsync(long userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetPermissionsForUserAsync(long userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetRoleCodesForUserAsync(long userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecUser>> ListUsersAsync(CancellationToken cancellationToken = default);
    Task<long> CreateUserAsync(SecUser user, IEnumerable<long> roleIds, long actorUserId, CancellationToken cancellationToken = default);
    Task UpdateUserAsync(SecUser user, IEnumerable<long> roleIds, long actorUserId, CancellationToken cancellationToken = default);
    Task SoftDeleteUserAsync(long userId, long actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecRole>> ListRolesAsync(CancellationToken cancellationToken = default);
    Task<long> CreateRoleAsync(SecRole role, IEnumerable<long> permissionIds, long actorUserId, CancellationToken cancellationToken = default);
    Task UpdateRoleAsync(SecRole role, IEnumerable<long> permissionIds, long actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecPermission>> ListPermissionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecModule>> ListModulesForNavigationAsync(long userId, CancellationToken cancellationToken = default);
    Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken = default);
    Task EnsureAdminPasswordAsync(string employeeId, string passwordHash, long actorUserId, CancellationToken cancellationToken = default);
    Task RecordLastLoginAsync(long userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> GetRoleIdsForUserAsync(long userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> GetPermissionIdsForRoleAsync(long roleId, CancellationToken cancellationToken = default);
}
