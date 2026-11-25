using System.Globalization;
using Microsoft.AspNetCore.Html;

namespace BiometricDashboard.Web.Utilities;

public static class PortalDateTimeFormat
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-US");

    public static DateTime ToLocal(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value.ToLocalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Local),
            _ => value,
        };

    public static string FormatDate(DateTime value) =>
        ToLocal(value).ToString("MMMM d, yyyy", DisplayCulture);

    public static string FormatTime(DateTime value) =>
        ToLocal(value).ToString("h:mm:ss tt", DisplayCulture);

    public static string FormatIso(DateTime value) =>
        ToLocal(value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static HtmlString ToHtml(DateTime? value)
    {
        if (value is null)
            return new HtmlString("—");

        var local = ToLocal(value.Value);
        var date = FormatDate(local);
        var time = FormatTime(local);
        return new HtmlString(
            $"""<span class="portal-dt-when"><span class="portal-dt-date"><i class="bi bi-calendar-event" aria-hidden="true"></i> {date}</span><span class="portal-dt-time"><i class="bi bi-clock" aria-hidden="true"></i> {time}</span></span>""");
    }
}
