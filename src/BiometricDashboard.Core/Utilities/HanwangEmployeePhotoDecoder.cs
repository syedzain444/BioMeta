using System.Text.RegularExpressions;

namespace BiometricDashboard.Core.Utilities;

/// <summary>Extracts JPEG/PNG/BMP from Hanwang HDCP GetEmployee blobs (e.g. photo_data 大头照).</summary>
public static class HanwangEmployeePhotoDecoder
{
    private static readonly string[] PhotoFieldNames =
    [
        "photo_data", "visible_photo", "head_photo", "user_photo",
        "picture_data", "enroll_photo", "photo", "image_data", "user_image",
    ];

    public static IReadOnlyList<string> PhotoFieldNameList => PhotoFieldNames;

    public static string? ExtractFirstPhotoBase64(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var field in PhotoFieldNames)
        {
            var val = ExtractQuotedField(raw, field);
            if (!string.IsNullOrWhiteSpace(val) && val.Length > 64)
                return val;
        }

        return null;
    }

    public static string? ExtractQuotedField(string raw, string fieldName)
    {
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(fieldName)) return null;
        var pattern = fieldName + @"=""([^""]*)""";
        var m = Regex.Match(raw, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static DecodedEmployeePhoto? TryDecodePhoto(string? base64OrRaw)
    {
        if (string.IsNullOrWhiteSpace(base64OrRaw)) return null;

        var cleaned = base64OrRaw.Replace("\r", "").Replace("\n", "").Trim();
        byte[] bytes;
        try
        {
            var pad = cleaned.Length % 4;
            if (pad > 0) cleaned += new string('=', 4 - pad);
            bytes = Convert.FromBase64String(cleaned);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length < 24) return null;

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return new DecodedEmployeePhoto(bytes, "image/jpeg", "jpeg");

        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return new DecodedEmployeePhoto(bytes, "image/png", "png");

        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return new DecodedEmployeePhoto(bytes, "image/bmp", "bmp");

        var jpegAt = IndexOf(bytes, [0xFF, 0xD8, 0xFF]);
        if (jpegAt >= 0 && jpegAt < bytes.Length - 100)
        {
            var slice = bytes.AsSpan(jpegAt).ToArray();
            return new DecodedEmployeePhoto(slice, "image/jpeg", "jpeg-embedded");
        }

        return null;
    }

    public static DecodedEmployeePhoto? TryExtractFromEmployeeResponse(string raw)
    {
        var b64 = ExtractFirstPhotoBase64(raw);
        return TryDecodePhoto(b64);
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }
}

public sealed record DecodedEmployeePhoto(byte[] Bytes, string ContentType, string Source);