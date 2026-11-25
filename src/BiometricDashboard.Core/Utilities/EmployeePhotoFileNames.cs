using System.Text.RegularExpressions;

namespace BiometricDashboard.Core.Utilities;

public static partial class EmployeePhotoFileNames
{
    public static string Build(string? displayName, string employeeId, string? contentType)
    {
        var namePart = SanitizeNamePart(displayName);
        var idPart = SanitizeIdPart(employeeId);
        var ext = ExtensionFromContentType(contentType);
        return $"{namePart}_{idPart}{ext}";
    }

    public static string SanitizeNamePart(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "Employee";
        var t = displayName.Trim();
        t = InvalidFileChars().Replace(t, "");
        t = Regex.Replace(t, @"\s+", " ");
        t = t.Replace(' ', '_');
        if (t.Length > 80)
            t = t[..80];
        return string.IsNullOrEmpty(t) ? "Employee" : t;
    }

    public static string SanitizeIdPart(string employeeId)
    {
        var id = NicIdNormalizer.Normalize(employeeId);
        if (string.IsNullOrEmpty(id))
            id = employeeId.Trim();
        id = InvalidFileChars().Replace(id, "");
        return string.IsNullOrEmpty(id) ? "unknown" : id;
    }

    public static string ExtensionFromContentType(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/bmp" => ".bmp",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };

    [GeneratedRegex(@"[\\/:*?""<>|]")]
    private static partial Regex InvalidFileChars();

}