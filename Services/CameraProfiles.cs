using Microsoft.Data.Sqlite;

namespace ContiExif.Services;

public sealed record CameraProfile(string Raw, string? Name, double? Multiplier);

/// <summary>
/// Custom camera names and focal multipliers (full-frame equivalent), stored in ContiExif-Cameras.db.
/// Photo files are never modified.
/// </summary>
public static class CameraProfiles
{
    public static string DbPath => Path.Combine(AppPaths.Root, "ContiExif-Cameras.db");

    private static Dictionary<string, CameraProfile> _map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WriteLock = new();

    private static SqliteConnection Open()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS cameras(raw TEXT PRIMARY KEY COLLATE NOCASE, name TEXT, mult REAL);";
        cmd.ExecuteNonQuery();
        return c;
    }

    public static void Load()
    {
        try
        {
            var map = new Dictionary<string, CameraProfile>(StringComparer.OrdinalIgnoreCase);
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT raw, name, mult FROM cameras";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string raw = r.GetString(0);
                string? name = r.IsDBNull(1) ? null : r.GetString(1);
                double? mult = r.IsDBNull(2) ? null : r.GetDouble(2);
                if (string.IsNullOrWhiteSpace(name)) name = null;
                if (mult is <= 0) mult = null;
                if (name != null || mult != null) map[raw] = new CameraProfile(raw, name, mult);
            }
            _map = map;
        }
        catch { }
    }

    public static CameraProfile? Get(string raw)
        => !string.IsNullOrEmpty(raw) && _map.TryGetValue(raw, out var p) ? p : null;

    public static string DisplayName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return _map.TryGetValue(raw, out var p) && p.Name != null ? p.Name : raw;
    }

    public static double Multiplier(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return 1.0;
        return _map.TryGetValue(raw, out var p) && p.Multiplier is double m ? m : 1.0;
    }

    /// <summary>Saves the same name/multiplier for every raw camera name given. Both null removes the profile.</summary>
    public static void Save(IEnumerable<string> raws, string? name, double? multiplier)
    {
        lock (WriteLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var raw in raws)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                if (name == null && multiplier == null)
                {
                    cmd.CommandText = "DELETE FROM cameras WHERE raw=$r";
                    cmd.Parameters.AddWithValue("$r", raw);
                }
                else
                {
                    cmd.CommandText = @"INSERT INTO cameras(raw,name,mult) VALUES($r,$n,$m)
                                        ON CONFLICT(raw) DO UPDATE SET name=excluded.name, mult=excluded.mult";
                    cmd.Parameters.AddWithValue("$r", raw);
                    cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$m", (object?)multiplier ?? DBNull.Value);
                }
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Load();
    }

    public static void Remove(IEnumerable<string> raws) => Save(raws, null, null);
}
