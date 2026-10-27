using System.Globalization;
using MeshAll.Server.Localization;

namespace MeshAll.Server.Services;

public static class DisplayFormat
{
    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    public static string Bytes(decimal bytes)
    {
        decimal value = Math.Max(0, bytes);
        int unit = 0;

        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {UiText.Get(ByteUnits[unit])}";
    }

    public static string Percent(double? percent) => percent is double value && double.IsFinite(value)
        ? UiText.Get("{0:0.#}%", Math.Clamp(value, 0, 100))
        : UiText.Get("Not reported");

    public static string CssNumber(double number) => number.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Uptime(long seconds)
    {
        long bounded = Math.Clamp(seconds, 0, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond);
        TimeSpan uptime = TimeSpan.FromSeconds(bounded);
        return UiText.Get("{0}d {1:00}h {2:00}m", uptime.Days, uptime.Hours, uptime.Minutes);
    }

    public static string RelativeTime(DateTimeOffset? time)
    {
        if (time is null || time <= DateTimeOffset.UnixEpoch)
        {
            return UiText.Get("Not reported");
        }

        TimeSpan age = DateTimeOffset.UtcNow - time.Value;
        if (age.TotalMinutes < 1)
        {
            return UiText.Get("Just now");
        }

        if (age.TotalHours < 1)
        {
            return UiText.Get("{0}m ago", (int)age.TotalMinutes);
        }

        return age.TotalDays < 1 ? UiText.Get("{0}h ago", (int)age.TotalHours) : UiText.Get("{0}d ago", (int)age.TotalDays);
    }
}
