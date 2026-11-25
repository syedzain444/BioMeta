using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BiometricDashboard.Infrastructure.Persistence;
using Dapper;
using Oracle.ManagedDataAccess.Client;

namespace BiometricDashboard.Infrastructure.Repositories;

public sealed class SecurityRepository(OracleConnectionFactory factory) : ISecurityRepository
{
    private const string UserSelect = """
        SELECT USER_ID UserId, EMPLOYEE_ID EmployeeId, PASSWORD_HASH PasswordHash, DISPLAY_NAME DisplayName,
               EMAIL Email, CASE WHEN IS_ACTIVE = 'Y' THEN 1 ELSE 0 END IsActive,
               CASE WHEN MUST_CHANGE_PWD = 'Y' THEN 1 ELSE 0 END MustChangePassword,
               LAST_LOGIN_AT LastLoginAt, CREATED_BY CreatedBy, UPDATED_BY UpdatedBy, DELETED_BY DeletedBy,
               CREATED_AT CreatedAt, UPDATED_AT UpdatedAt, DELETED_AT DeletedAt
        FROM SEC_USER WHERE DELETED_AT IS NULL
        """;

    public async Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SEC_USER WHERE DELETED_AT IS NULL") > 0;
    }

    public async Task<SecUser?> GetUserByEmployeeIdAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var row = await conn.QueryFirstOrDefaultAsync<UserDbRow>(
            UserSelect + " AND UPPER(TRIM(EMPLOYEE_ID)) = UPPER(TRIM(:EmployeeId))",
            new { EmployeeId = employeeId });
        return row is null ? null : MapUser(row);
    }

    public async Task<SecUser?> GetUserByIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var row = await conn.QueryFirstOrDefaultAsync<UserDbRow>(
            UserSelect + " AND USER_ID = :UserId",
            new { UserId = userId });
        return row is null ? null : MapUser(row);
    }

    public async Task<IReadOnlyList<string>> GetPermissionsForUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<string>(
            """
            SELECT DISTINCT p.PERMISSION_CODE
            FROM SEC_USER_ROLE ur
            JOIN SEC_ROLE r ON r.ROLE_ID = ur.ROLE_ID AND r.DELETED_AT IS NULL AND r.IS_ACTIVE = 'Y'
            JOIN SEC_ROLE_PERMISSION rp ON rp.ROLE_ID = r.ROLE_ID AND rp.DELETED_AT IS NULL
            JOIN SEC_PERMISSION p ON p.PERMISSION_ID = rp.PERMISSION_ID AND p.DELETED_AT IS NULL AND p.IS_ACTIVE = 'Y'
            WHERE ur.USER_ID = :UserId AND ur.DELETED_AT IS NULL
            """,
            new { UserId = userId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<string>> GetRoleCodesForUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var rows = await conn.QueryAsync<string>(
            """
            SELECT r.ROLE_CODE FROM SEC_USER_ROLE ur
            JOIN SEC_ROLE r ON r.ROLE_ID = ur.ROLE_ID AND r.DELETED_AT IS NULL
            WHERE ur.USER_ID = :UserId AND ur.DELETED_AT IS NULL
            """,
            new { UserId = userId });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SecUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        var users = (await conn.QueryAsync<UserDbRow>(UserSelect + " ORDER BY DISPLAY_NAME"))
            .Select(MapUser)
            .ToList();
        foreach (var u in users)
            u.RoleCodes = await GetRoleCodesForUserAsync(u.UserId, cancellationToken);
        return users;
    }

    public async Task<long> CreateUserAsync(SecUser user, IEnumerable<long> roleIds, long actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            INSERT INTO SEC_USER (EMPLOYEE_ID, PASSWORD_HASH, DISPLAY_NAME, EMAIL, IS_ACTIVE, MUST_CHANGE_PWD, CREATED_BY)
            VALUES (:EmployeeId, :PasswordHash, :DisplayName, :Email, :IsActive, :MustChangePwd, :CreatedBy)
            """,
            new
            {
                user.EmployeeId,
                user.PasswordHash,
                user.DisplayName,
                user.Email,
                IsActive = user.IsActive ? "Y" : "N",
                MustChangePwd = user.MustChangePassword ? "Y" : "N",
                CreatedBy = actorUserId,
            },
            tx);

        var id = await conn.ExecuteScalarAsync<long>(
            "SELECT USER_ID FROM SEC_USER WHERE EMPLOYEE_ID = :EmployeeId AND DELETED_AT IS NULL",
            new { user.EmployeeId },
            tx);

        await ReplaceUserRolesAsync(conn, tx, id, roleIds, actorUserId);
        await tx.CommitAsync(cancellationToken);
        return id;
    }

    public async Task UpdateUserAsync(SecUser user, IEnumerable<long> roleIds, long actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            UPDATE SEC_USER SET DISPLAY_NAME = :DisplayName, EMAIL = :Email, IS_ACTIVE = :IsActive,
              MUST_CHANGE_PWD = :MustChangePwd, UPDATED_BY = :UpdatedBy, UPDATED_AT = SYSTIMESTAMP
            WHERE USER_ID = :UserId AND DELETED_AT IS NULL
            """,
            new
            {
                user.DisplayName,
                user.Email,
                IsActive = user.IsActive ? "Y" : "N",
                MustChangePwd = user.MustChangePassword ? "Y" : "N",
                UpdatedBy = actorUserId,
                user.UserId,
            },
            tx);

        if (!string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            await conn.ExecuteAsync(
                "UPDATE SEC_USER SET PASSWORD_HASH = :PasswordHash, UPDATED_BY = :UpdatedBy, UPDATED_AT = SYSTIMESTAMP WHERE USER_ID = :UserId",
                new { user.PasswordHash, UpdatedBy = actorUserId, user.UserId },
                tx);
        }

        await ReplaceUserRolesAsync(conn, tx, user.UserId, roleIds, actorUserId);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task SoftDeleteUserAsync(long userId, long actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(
            """
            UPDATE SEC_USER SET DELETED_AT = SYSTIMESTAMP, DELETED_BY = :Actor, IS_ACTIVE = 'N', UPDATED_AT = SYSTIMESTAMP, UPDATED_BY = :Actor
            WHERE USER_ID = :UserId
            """,
            new { Actor = actorUserId, UserId = userId });
    }

    public async Task<IReadOnlyList<SecRole>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        return (await conn.QueryAsync<SecRole>(
            """
            SELECT ROLE_ID RoleId, ROLE_CODE RoleCode, ROLE_NAME RoleName, DESCRIPTION Description,
                   CASE WHEN IS_SYSTEM = 'Y' THEN 1 ELSE 0 END IsSystem,
                   CASE WHEN IS_ACTIVE = 'Y' THEN 1 ELSE 0 END IsActive,
                   CREATED_BY CreatedBy, UPDATED_BY UpdatedBy, DELETED_BY DeletedBy,
                   CREATED_AT CreatedAt, UPDATED_AT UpdatedAt, DELETED_AT DeletedAt
            FROM SEC_ROLE WHERE DELETED_AT IS NULL ORDER BY ROLE_NAME
            """)).ToList();
    }

    public async Task<long> CreateRoleAsync(SecRole role, IEnumerable<long> permissionIds, long actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            INSERT INTO SEC_ROLE (ROLE_CODE, ROLE_NAME, DESCRIPTION, IS_SYSTEM, IS_ACTIVE, CREATED_BY)
            VALUES (:RoleCode, :RoleName, :Description, 'N', :IsActive, :CreatedBy)
            """,
            new
            {
                role.RoleCode,
                role.RoleName,
                role.Description,
                IsActive = role.IsActive ? "Y" : "N",
                CreatedBy = actorUserId,
            },
            tx);

        var roleId = await conn.ExecuteScalarAsync<long>(
            "SELECT ROLE_ID FROM SEC_ROLE WHERE ROLE_CODE = :RoleCode AND DELETED_AT IS NULL",
            new { role.RoleCode },
            tx);

        await ReplaceRolePermissionsAsync(conn, tx, roleId, permissionIds, actorUserId);
        await tx.CommitAsync(cancellationToken);
        return roleId;
    }

    public async Task UpdateRoleAsync(SecRole role, IEnumerable<long> permissionIds, long actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(cancellationToken);

        await conn.ExecuteAsync(
            """
            UPDATE SEC_ROLE SET ROLE_NAME = :RoleName, DESCRIPTION = :Description, IS_ACTIVE = :IsActive,
              UPDATED_BY = :UpdatedBy, UPDATED_AT = SYSTIMESTAMP
            WHERE ROLE_ID = :RoleId AND DELETED_AT IS NULL
            """,
            new
            {
                role.RoleName,
                role.Description,
                IsActive = role.IsActive ? "Y" : "N",
                UpdatedBy = actorUserId,
                role.RoleId,
            },
            tx);

        await ReplaceRolePermissionsAsync(conn, tx, role.RoleId, permissionIds, actorUserId);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SecPermission>> ListPermissionsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        return (await conn.QueryAsync<SecPermission>(
            """
            SELECT p.PERMISSION_ID PermissionId, p.MODULE_ID ModuleId, p.PERMISSION_CODE PermissionCode,
                   p.PERMISSION_NAME PermissionName, p.DESCRIPTION Description,
                   CASE WHEN p.IS_ACTIVE = 'Y' THEN 1 ELSE 0 END IsActive,
                   m.MODULE_NAME ModuleName
            FROM SEC_PERMISSION p
            JOIN SEC_MODULE m ON m.MODULE_ID = p.MODULE_ID AND m.DELETED_AT IS NULL
            WHERE p.DELETED_AT IS NULL
            ORDER BY m.SORT_ORDER, p.PERMISSION_CODE
            """)).ToList();
    }

    public async Task<IReadOnlyList<SecModule>> ListModulesForNavigationAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        return (await conn.QueryAsync<SecModule>(
            """
            SELECT DISTINCT m.MODULE_ID ModuleId, m.MODULE_CODE ModuleCode, m.MODULE_NAME ModuleName,
                   m.ROUTE_PATH RoutePath, m.ICON_CLASS IconClass, m.SORT_ORDER SortOrder,
                   CASE WHEN m.IS_ACTIVE = 'Y' THEN 1 ELSE 0 END IsActive
            FROM SEC_MODULE m
            JOIN SEC_PERMISSION p ON p.MODULE_ID = m.MODULE_ID AND p.DELETED_AT IS NULL
            JOIN SEC_ROLE_PERMISSION rp ON rp.PERMISSION_ID = p.PERMISSION_ID AND rp.DELETED_AT IS NULL
            JOIN SEC_USER_ROLE ur ON ur.ROLE_ID = rp.ROLE_ID AND ur.DELETED_AT IS NULL
            WHERE ur.USER_ID = :UserId AND m.DELETED_AT IS NULL AND m.IS_ACTIVE = 'Y'
            ORDER BY m.SORT_ORDER
            """,
            new { UserId = userId })).ToList();
    }

    public async Task EnsureAdminPasswordAsync(string employeeId, string passwordHash, long actorUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(
            """
            UPDATE SEC_USER SET PASSWORD_HASH = :PasswordHash, UPDATED_BY = :Actor, UPDATED_AT = SYSTIMESTAMP
            WHERE UPPER(EMPLOYEE_ID) = UPPER(:EmployeeId) AND DELETED_AT IS NULL
            """,
            new { PasswordHash = passwordHash, Actor = actorUserId, EmployeeId = employeeId });
    }

    public async Task RecordLastLoginAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(
            "UPDATE SEC_USER SET LAST_LOGIN_AT = SYSTIMESTAMP WHERE USER_ID = :UserId",
            new { UserId = userId });
    }

    public async Task<IReadOnlyList<long>> GetRoleIdsForUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        return (await conn.QueryAsync<long>(
            "SELECT ROLE_ID FROM SEC_USER_ROLE WHERE USER_ID = :UserId AND DELETED_AT IS NULL",
            new { UserId = userId })).ToList();
    }

    public async Task<IReadOnlyList<long>> GetPermissionIdsForRoleAsync(long roleId, CancellationToken cancellationToken = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(cancellationToken);
        return (await conn.QueryAsync<long>(
            "SELECT PERMISSION_ID FROM SEC_ROLE_PERMISSION WHERE ROLE_ID = :RoleId AND DELETED_AT IS NULL",
            new { RoleId = roleId })).ToList();
    }

    private static async Task ReplaceUserRolesAsync(
        OracleConnection conn,
        OracleTransaction tx,
        long userId,
        IEnumerable<long> roleIds,
        long actorUserId)
    {
        await conn.ExecuteAsync(
            "UPDATE SEC_USER_ROLE SET DELETED_AT = SYSTIMESTAMP, DELETED_BY = :Actor WHERE USER_ID = :UserId AND DELETED_AT IS NULL",
            new { Actor = actorUserId, UserId = userId },
            tx);

        foreach (var roleId in roleIds.Distinct())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO SEC_USER_ROLE (USER_ID, ROLE_ID, CREATED_BY)
                VALUES (:UserId, :RoleId, :CreatedBy)
                """,
                new { UserId = userId, RoleId = roleId, CreatedBy = actorUserId },
                tx);
        }
    }

    private static SecUser MapUser(UserDbRow row) => new()
    {
        UserId = row.UserId,
        EmployeeId = row.EmployeeId?.Trim() ?? "",
        PasswordHash = row.PasswordHash ?? "",
        DisplayName = row.DisplayName ?? "",
        Email = row.Email,
        IsActive = row.IsActive != 0,
        MustChangePassword = row.MustChangePassword != 0,
        LastLoginAt = row.LastLoginAt,
        CreatedBy = row.CreatedBy,
        UpdatedBy = row.UpdatedBy,
        DeletedBy = row.DeletedBy,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
    };

    private sealed class UserDbRow
    {
        public long UserId { get; set; }
        public string? EmployeeId { get; set; }
        public string? PasswordHash { get; set; }
        public string? DisplayName { get; set; }
        public string? Email { get; set; }
        public int IsActive { get; set; }
        public int MustChangePassword { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public long? CreatedBy { get; set; }
        public long? UpdatedBy { get; set; }
        public long? DeletedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    private static async Task ReplaceRolePermissionsAsync(
        OracleConnection conn,
        OracleTransaction tx,
        long roleId,
        IEnumerable<long> permissionIds,
        long actorUserId)
    {
        await conn.ExecuteAsync(
            "UPDATE SEC_ROLE_PERMISSION SET DELETED_AT = SYSTIMESTAMP, DELETED_BY = :Actor WHERE ROLE_ID = :RoleId AND DELETED_AT IS NULL",
            new { Actor = actorUserId, RoleId = roleId },
            tx);

        foreach (var permId in permissionIds.Distinct())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO SEC_ROLE_PERMISSION (ROLE_ID, PERMISSION_ID, CREATED_BY)
                VALUES (:RoleId, :PermissionId, :CreatedBy)
                """,
                new { RoleId = roleId, PermissionId = permId, CreatedBy = actorUserId },
                tx);
        }
    }
}
