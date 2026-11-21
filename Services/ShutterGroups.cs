namespace ContiExif.Services;

/// <summary>
/// Shutter speed groups. A group key is an integer:
///   key &lt; 0  : fast speeds, -key is the group's denominator (-1000 = 1/1000 to 1/1999)
///   key &gt;= 1 : slow speeds in seconds (1 = 1-1.99 s, 10 = 10-19 s, 100 = 100-149 s, 150 = 150-199 s)
/// </summary>
public static class ShutterGroups
{
    public static long Bucket(double t)
    {
        if (t >= 1)
        {
            if (t < 10) return (long)Math.Floor(t);
            if (t < 100) return (long)(Math.Floor(t / 10) * 10);
            return (long)(Math.Floor(t / 50) * 50);
        }

        double d = Math.Round(1 / t, 3);
        if (d < 2) d = 2;
        long di = (long)Math.Floor(d);
        long k = Pow10Floor(di);
        return -(di / k * k);
    }

    public static string Label(long key)
    {
        if (key >= 1)
        {
            if (key < 10) return $"{key} s";
            if (key < 100) return $"{key} – {key + 9} s";
            return $"{key} – {key + 49} s";
        }
        long b = -key;
        long k = Pow10Floor(b);
        return k <= 1 ? $"1/{b}" : $"1/{b} – 1/{b + k - 1}";
    }

    /// <summary>Compact label for slider ends: 1/1000, 1/60, 2 s, 20 s.</summary>
    public static string ShortLabel(long key) => key >= 1 ? $"{key} s" : $"1/{-key}";

    private static long Pow10Floor(long d)
    {
        long k = 1;
        while (k * 10 <= d) k *= 10;
        return k;
    }
}
