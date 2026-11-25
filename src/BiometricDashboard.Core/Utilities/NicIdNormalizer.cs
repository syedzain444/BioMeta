namespace BiometricDashboard.Core.Utilities;

public static class NicIdNormalizer
{
    /// <summary>Portal/device employee id = NIC without dashes (digits only).</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var digits = new char[raw.Length];
        var n = 0;
        foreach (var ch in raw.Trim())
        {
            if (ch is >= '0' and <= '9')
                digits[n++] = ch;
        }
        return n == 0 ? raw.Trim() : new string(digits, 0, n);
    }
}
