using BiometricDashboard.Core.Entities;
using BiometricDashboard.Core.Utilities;

namespace BiometricDashboard.Infrastructure.Device;

internal static class HanwangEmployeeUploadFormatter
{
    internal static string BuildEmployeeCommand(DeviceEmployeeRecord employee)
    {
        var id = NicIdNormalizer.Normalize(employee.EmployeeId);
        var name = Escape(employee.Name);
        var auth = Escape(employee.Authority ?? "0X11");
        var card = Escape(employee.CardNumber ?? "0Xffffffff");
        var dept = Escape(employee.Department ?? "");
        var check = Escape(employee.CheckType ?? "face");

        var head =
            $"Employee(id=\"{id}\" name=\"{name}\" authority=\"{auth}\" card_num=\"{card}\" " +
            $"calid=\"0\" check_type=\"{check}\" opendoor_type=\"{check}\" password=\"\" type=\"M2000\" alg_edition=\"3.1\"";

        if (!string.IsNullOrWhiteSpace(employee.FaceDataBase64))
        {
            var face = employee.FaceDataBase64!.Replace("\r", "").Replace("\n", "");
            return head + $"\r\nface_data=\"{face}\")";
        }

        if (!string.IsNullOrWhiteSpace(dept))
            return head + $" department=\"{dept}\")";

        return head + ")";
    }

    internal static IReadOnlyList<string> BuildHdcpFallbackCommands(DeviceEmployeeRecord employee)
    {
        var id = NicIdNormalizer.Normalize(employee.EmployeeId);
        var name = Escape(employee.Name);
        var auth = Escape(employee.Authority ?? "0X11");
        var card = Escape(employee.CardNumber ?? "0Xffffffff");
        return
        [
            BuildEmployeeCommand(employee),
            $"SetEmployee(employee_id=\"{id}\" name=\"{name}\" authority=\"{auth}\" card_num=\"{card}\")",
            $"ModifyEmployee(employee_id=\"{id}\" name=\"{name}\" authority=\"{auth}\" card_num=\"{card}\")",
        ];
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}