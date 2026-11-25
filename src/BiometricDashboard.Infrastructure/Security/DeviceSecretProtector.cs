using Microsoft.AspNetCore.DataProtection;

namespace BiometricDashboard.Infrastructure.Security;

public sealed class DeviceSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("BiometricDashboard.DeviceAdminPassword.v1");

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}
