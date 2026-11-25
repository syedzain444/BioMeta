using System.Globalization;
using System.Text.RegularExpressions;
using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Utilities;

namespace BiometricDashboard.Infrastructure.Device;

internal static partial class HanwangProtocolParser
{
    private static readonly Regex FieldRegex = FieldPattern();

    public static Dictionary<string, string> ParseFields(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FieldRegex.Matches(text))
            dict[m.Groups[1].Value] = m.Groups[2].Value;
        return dict;
    }

    public static (Dictionary<string, string> Header, List<Dictionary<string, string>> Rows) ParseReturn(string raw)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Dictionary<string, string>>();
        if (!raw.StartsWith("Return(", StringComparison.Ordinal))
            return (header, rows);

        var inner = raw.AsSpan(7, raw.LastIndexOf(')') - 7).ToString();
        foreach (var line in inner.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = ParseFields(line);
            if (fields.Count == 0) continue;
            if (fields.ContainsKey("time") && fields.ContainsKey("id"))
                rows.Add(fields);
            else if (rows.Count == 0)
                foreach (var kv in fields) header[kv.Key] = kv.Value;
        }
        return (header, rows);
    }

    public static AttendanceRecord? ToAttendance(Dictionary<string, string> row, string deviceSerial)
    {
        if (!row.TryGetValue("time", out var timeStr) || !row.TryGetValue("id", out var empId))
            return null;
        if (!DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var punch))
            return null;

        row.TryGetValue("name", out var name);
        row.TryGetValue("workcode", out var workCode);
        row.TryGetValue("status", out var status);
        row.TryGetValue("authority", out var authority);
        row.TryGetValue("card_src", out var cardSrc);

        return new AttendanceRecord
        {
            DeviceSerial = deviceSerial,
            EmployeeId = empId,
            EmployeeName = name ?? "",
            PunchTime = punch,
            WorkCode = workCode ?? "",
            Status = status ?? "",
            Authority = authority ?? "",
            CardSource = cardSrc ?? "",
            SyncedAt = DateTime.UtcNow,
        };
    }

    public static List<Dictionary<string, string>> ParseReturnEmployeeRows(string raw)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!raw.StartsWith("Return(", StringComparison.Ordinal))
            return rows;

        var inner = raw.AsSpan(7, raw.LastIndexOf(')') - 7).ToString();
        foreach (var line in inner.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = ParseFields(line);
            if (fields.Count == 0) continue;
            if (fields.ContainsKey("time") && fields.ContainsKey("id"))
                continue;
            if (!fields.ContainsKey("id") && !fields.ContainsKey("employee_id"))
                continue;
            rows.Add(fields);
        }

        return rows;
    }

    public static DeviceEmployeeRecord? ToDeviceEmployeeFromFields(IReadOnlyDictionary<string, string> fields)
    {
        var id = fields.GetValueOrDefault("id") ?? fields.GetValueOrDefault("employee_id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var (enrolledAt, enrolledSource) = TryParseEnrolledAt(fields);
        return new DeviceEmployeeRecord
        {
            EmployeeId = NicIdNormalizer.Normalize(id),
            Name = fields.GetValueOrDefault("name") ?? "",
            CardNumber = NullIfEmpty(fields.GetValueOrDefault("card_number") ?? fields.GetValueOrDefault("card_num") ?? fields.GetValueOrDefault("card")),
            Department = NullIfEmpty(fields.GetValueOrDefault("department")),
            Authority = NullIfEmpty(fields.GetValueOrDefault("authority")),
            Password = NullIfEmpty(fields.GetValueOrDefault("password")),
            CheckType = NullIfEmpty(fields.GetValueOrDefault("check_type") ?? fields.GetValueOrDefault("opendoor_type")),
            FaceTemplateCount = 0,
            FoundOnDevice = true,
            EnrolledAtUtc = enrolledAt,
            EnrolledAtSource = enrolledSource,
        };
    }

    public static DeviceEmployeeRecord? ToDeviceEmployee(string raw, string requestedId)
    {
        if (!raw.Contains("success", StringComparison.OrdinalIgnoreCase))
            return null;
        if (raw.Contains("unknown id", StringComparison.OrdinalIgnoreCase))
            return null;

        var head = raw.Split("face_data=", StringSplitOptions.None)[0];
        var fields = ParseFields(head.Replace('\n', ' '));
        var id = fields.GetValueOrDefault("id")
                 ?? fields.GetValueOrDefault("employee_id")
                 ?? requestedId;
        var faceCount = FaceDataPattern().Matches(raw).Count;
        var faceData = HanwangEmployeePhotoDecoder.ExtractQuotedField(raw, "face_data");
        var photoB64 = HanwangEmployeePhotoDecoder.ExtractFirstPhotoBase64(raw);
        var (enrolledAt, enrolledSource) = TryParseEnrolledAt(fields);

        return new DeviceEmployeeRecord
        {
            EmployeeId = NicIdNormalizer.Normalize(id),
            Name = fields.GetValueOrDefault("name") ?? "",
            CardNumber = NullIfEmpty(
                fields.GetValueOrDefault("card_number")
                ?? fields.GetValueOrDefault("card_num")
                ?? fields.GetValueOrDefault("card")),
            Department = NullIfEmpty(fields.GetValueOrDefault("department")),
            Authority = NullIfEmpty(fields.GetValueOrDefault("authority")),
            Password = NullIfEmpty(fields.GetValueOrDefault("password")),
            CheckType = NullIfEmpty(fields.GetValueOrDefault("check_type") ?? fields.GetValueOrDefault("opendoor_type")),
            FaceTemplateCount = faceCount,
            FaceDataBase64 = faceData,
            PhotoDataBase64 = photoB64,
            FoundOnDevice = true,
            EnrolledAtUtc = enrolledAt,
            EnrolledAtSource = enrolledSource,
        };
    }

    private static (DateTime? Utc, string? Source) TryParseEnrolledAt(IReadOnlyDictionary<string, string> fields)
    {
        ReadOnlySpan<string> keys =
        [
            "create_time", "register_time", "registration_time", "enroll_time", "enrolled_time",
            "user_time", "valid_start", "valid_from", "start_date", "register_date",
        ];
        foreach (var key in keys)
        {
            if (!fields.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;
            if (TryParseDeviceDateTime(raw, out var dt))
                return (dt, "Device register time");
        }

        return (null, null);
    }

    private static bool TryParseDeviceDateTime(string raw, out DateTime utc)
    {
        utc = default;
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd", "yyyy/MM/dd",
        };
        if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            || DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out local))
        {
            utc = local.Kind switch
            {
                DateTimeKind.Utc => local,
                DateTimeKind.Local => local.ToUniversalTime(),
                _ => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime(),
            };
            return true;
        }

        return false;
    }

    private static string? NullIfEmpty(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    [GeneratedRegex(@"(\w+)=""([^""]*)""", RegexOptions.Compiled)]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"face_data=""", RegexOptions.Compiled)]
    private static partial Regex FaceDataPattern();
}
