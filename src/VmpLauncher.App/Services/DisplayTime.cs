using System.Globalization;

namespace VmpLauncher.App.Services;

/// <summary>
/// The one place the UI formats dates and times: the PC's local time zone, 12-hour clock with AM/PM, date in the
/// user's culture. Stored/log/file values stay UTC; convert only here, at display time.
/// </summary>
public static class DisplayTime
{
    public static string Time(DateTime value)
    {
        var local = ToLocal(value);
        var hour = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return string.Create(CultureInfo.InvariantCulture, $"{hour}:{local.Minute:00} {(local.Hour < 12 ? "AM" : "PM")}");
    }

    public static string DateTimeText(DateTime value, CultureInfo? culture = null)
    {
        var local = ToLocal(value);
        return local.ToString("d", culture ?? CultureInfo.CurrentCulture) + ", " + Time(local);
    }

    /// <summary>Unspecified kinds are taken as local (DateTime.Now); Utc values are converted.</summary>
    private static DateTime ToLocal(DateTime v) => v.Kind == DateTimeKind.Utc ? v.ToLocalTime() : v;
}
