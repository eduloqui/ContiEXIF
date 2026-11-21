using System.Globalization;

namespace ContiExif.Services;

public static class Formatters
{
    public static string Shutter(double s)
        => s <= 0 ? "–" : s >= 1 ? $"{s:0.#}s" : $"1/{Math.Round(1 / s):0}";

    public static string Aperture(double a) => $"f/{a:0.0}";
    public static string Focal(double f) => $"{f:0} mm";

    public static string Size(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    public static string Label(string key, object? v)
    {
        if (v is null) return "Unknown";
        switch (key)
        {
            case "camera":
            case "lens":
                var s = v.ToString() ?? "";
                return s.Length == 0 ? "Unknown" : s;
            case "focal": return Focal(Convert.ToDouble(v));
            case "aperture": return Aperture(Convert.ToDouble(v));
            case "shutter": return ShutterGroups.Label(Convert.ToInt64(v));
            case "iso":
                long b = Convert.ToInt64(v);
                return b <= 0 ? "Under 100" : $"{b} – {b + 99}";
            case "month":
                int m = (int)Convert.ToInt64(v);
                return m is >= 1 and <= 12 ? CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(m) : m.ToString();
            case "hour":
                int h = (int)Convert.ToInt64(v);
                return $"{h:00}:00 – {h:00}:59";
            default: return Convert.ToString(v, CultureInfo.CurrentCulture) ?? "";
        }
    }

    /// <summary>Compact label of a value, used at the ends of the range sliders.</summary>
    public static string RangeLabel(string key, object? v)
    {
        if (v is null) return "";
        switch (key)
        {
            case "focal": return $"{Convert.ToDouble(v):0} mm";
            case "aperture": return Aperture(Convert.ToDouble(v));
            case "shutter": return ShutterGroups.ShortLabel(Convert.ToInt64(v));
            case "iso":
                long b = Convert.ToInt64(v);
                return b <= 0 ? "<100" : b.ToString();
            default: return Label(key, v);
        }
    }
}
