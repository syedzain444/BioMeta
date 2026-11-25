using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Interfaces;
using BCryptNet = BCrypt.Net.BCrypt;

namespace BiometricDashboard.Infrastructure.Security;

public sealed class AuthService(ISecurityRepository repository) : IAuthService
{
    public string HashPassword(string plainPassword) => BCryptNet.HashPassword(plainPassword, workFactor: 11);

    public bool VerifyPassword(string plainPassword, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Contains("PLACEHOLDER", StringComparison.Ordinal))
            return false;
        return BCryptNet.Verify(plainPassword, hash);
    }

    public async Task<SecUser?> ValidateLoginAsync(string employeeId, string password, CancellationToken cancellationToken = default)
    {
        var user = await repository.GetUserByEmployeeIdAsync(employeeId.Trim(), cancellationToken);
        if (user is null || !user.IsActive)
            return null;
        if (!VerifyPassword(password, user.PasswordHash))
            return null;

        user.Permissions = await repository.GetPermissionsForUserAsync(user.UserId, cancellationToken);
        user.RoleCodes = await repository.GetRoleCodesForUserAsync(user.UserId, cancellationToken);
        return user;
    }

    public Task RecordLastLoginAsync(long userId, CancellationToken cancellationToken = default) =>
        repository.RecordLastLoginAsync(userId, cancellationToken);
}
