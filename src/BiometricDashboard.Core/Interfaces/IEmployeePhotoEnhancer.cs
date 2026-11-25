namespace BiometricDashboard.Core.Interfaces;

public interface IEmployeePhotoEnhancer
{
    /// <summary>White background + mild clarity; does not reshape the face.</summary>
    byte[] EnhanceForExport(ReadOnlySpan<byte> original, string? contentType);
}