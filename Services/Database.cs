using ContiExif.Models;
using Microsoft.Data.Sqlite;

namespace ContiExif.Services;

public sealed class Database
{
    public static readonly string[] FacetColumns =
        { "camera", "lens", "focal", "aperture", "shutter", "iso", "year", "month", "day", "hour" };

    /// <summary>Rows with a lower exifv are re-read on the next scan (v2 = decimal focal lengths + tags).</summary>
    private const int ExifVersion = 2;

    private const string Schema = @"
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS photos(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  path TEXT NOT NULL UNIQUE COLLATE NOCASE,
  name TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
  taken TEXT NOT NULL, year INTEGER, month INTEGER, day INTEGER, hour INTEGER,
  camera TEXT NOT NULL DEFAULT '', lens TEXT NOT NULL DEFAULT '',
  focal REAL, aperture REAL, shutter REAL, iso INTEGER, width INTEGER, height INTEGER,
  tags TEXT NOT NULL DEFAULT '', exifv INTEGER NOT NULL DEFAULT 2);
CREATE INDEX IF NOT EXISTS ix_taken ON photos(taken);
CREATE INDEX IF NOT EXISTS ix_focal ON photos(focal);
CREATE INDEX IF NOT EXISTS ix_aperture ON photos(aperture);
CREATE INDEX IF NOT EXISTS ix_shutter ON photos(shutter);
CREATE INDEX IF NOT EXISTS ix_iso ON photos(iso);";

    private const string Cols =
        "id,path,name,size,mtime,taken,year,month,day,hour,camera,lens,focal,aperture,shutter,iso,width,height,tags";

    private const string UpsertSql = @"
INSERT INTO photos(path,name,size,mtime,taken,year,month,day,hour,camera,lens,focal,aperture,shutter,iso,width,height,tags,exifv)
VALUES($path,$name,$size,$mtime,$taken,$year,$month,$day,$hour,$camera,$lens,$focal,$aperture,$shutter,$iso,$width,$height,$tags,$exifv)
ON CONFLICT(path) DO UPDATE SET name=excluded.name,size=excluded.size,mtime=excluded.mtime,taken=excluded.taken,
year=excluded.year,month=excluded.month,day=excluded.day,hour=excluded.hour,camera=excluded.camera,lens=excluded.lens,
focal=excluded.focal,aperture=excluded.aperture,shutter=excluded.shutter,iso=excluded.iso,width=excluded.width,height=excluded.height,
tags=excluded.tags,exifv=excluded.exifv;";

    private readonly string _cs;

    public Database(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, Schema);

        // databases created by older versions: add the new columns (old rows are re-read on the next scan)
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(photos)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        if (!cols.Contains("tags")) Exec(c, "ALTER TABLE photos ADD COLUMN tags TEXT NOT NULL DEFAULT ''");
        if (!cols.Contains("exifv")) Exec(c, "ALTER TABLE photos ADD COLUMN exifv INTEGER NOT NULL DEFAULT 1");
    }

    public string FilePath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA synchronous=NORMAL;");

        // SQL helpers used by filters, sorting and search (they read the camera profiles in memory)
        c.CreateFunction("camname", (string raw) => CameraProfiles.DisplayName(raw));
        c.CreateFunction("fequiv", (double focal, string raw) => focal < 0 ? -1.0 : focal * CameraProfiles.Multiplier(raw));
        c.CreateFunction("shbucket", (double t) => t > 0 ? ShutterGroups.Bucket(t) : 0L);
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object V(object? o) => o ?? DBNull.Value;

    /// <summary>SQL expression of a filter column (what the filter list groups and matches on).</summary>
    private static string Expr(string col) => col switch
    {
        "camera" => "camname(camera)",
        "focal" => "CAST(ROUND(NULLIF(fequiv(COALESCE(focal,-1.0), camera), -1.0)) AS INTEGER)",
        "shutter" => "NULLIF(shbucket(COALESCE(shutter,0.0)), 0)",
        "iso" => "(iso/100)*100",
        _ => col
    };

    public string? Root
    {
        get
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='root'";
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetRoot(string root)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES('root',$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$v", root);
        cmd.ExecuteNonQuery();
    }

    public static string? ReadRoot(string path)
    {
        try { return File.Exists(path) ? new Database(path).Root : null; }
        catch { return null; }
    }

    public Dictionary<string, (long Size, long MTime, bool Outdated)> LoadIndex()
    {
        var d = new Dictionary<string, (long, long, bool)>(StringComparer.OrdinalIgnoreCase);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path,size,mtime,exifv FROM photos";
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt32(3) < ExifVersion);
        return d;
    }

    public int CountOutdated()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM photos WHERE exifv < {ExifVersion}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Upsert(IReadOnlyList<PhotoRecord> batch)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = UpsertSql;
        string[] names = { "$path", "$name", "$size", "$mtime", "$taken", "$year", "$month", "$day", "$hour",
            "$camera", "$lens", "$focal", "$aperture", "$shutter", "$iso", "$width", "$height", "$tags", "$exifv" };
        var ps = names.Select(n => { var p = cmd.CreateParameter(); p.ParameterName = n; cmd.Parameters.Add(p); return p; }).ToArray();
        foreach (var r in batch)
        {
            ps[0].Value = r.Path; ps[1].Value = r.Name; ps[2].Value = r.Size; ps[3].Value = r.MTime;
            ps[4].Value = r.Taken; ps[5].Value = V(r.Year); ps[6].Value = V(r.Month); ps[7].Value = V(r.Day);
            ps[8].Value = V(r.Hour); ps[9].Value = r.Camera; ps[10].Value = r.Lens; ps[11].Value = V(r.Focal);
            ps[12].Value = V(r.Aperture); ps[13].Value = V(r.Shutter); ps[14].Value = V(r.Iso);
            ps[15].Value = V(r.Width); ps[16].Value = V(r.Height); ps[17].Value = r.Tags; ps[18].Value = ExifVersion;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Delete(IEnumerable<string> paths)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM photos WHERE path=$p";
        var p = cmd.CreateParameter(); p.ParameterName = "$p"; cmd.Parameters.Add(p);
        foreach (var s in paths) { p.Value = s; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public List<string> Distinct(string col)
    {
        if (col != "camera" && col != "lens") throw new ArgumentException("Bad column");
        var list = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT DISTINCT {col} FROM photos WHERE {col} <> '' ORDER BY {col}";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Original (EXIF) camera names behind a displayed camera name.</summary>
    public List<string> RawCamerasFor(string display)
    {
        var list = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT camera FROM photos WHERE camname(camera) = $n AND camera <> '' ORDER BY camera";
        cmd.Parameters.AddWithValue("$n", display);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<(string Path, long Size, long MTime)> LoadThumbWork()
    {
        var list = new List<(string, long, long)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path,size,mtime FROM photos ORDER BY taken DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    public int CountAll()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM photos";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public Dictionary<string, List<(object? Value, long Count)>> LoadFacets(
        IReadOnlyDictionary<string, List<object?>> sel, string? search,
        IReadOnlyDictionary<string, (object Low, object High)>? ranges = null)
    {
        var res = new Dictionary<string, List<(object?, long)>>();
        using var c = Open();
        foreach (var col in FacetColumns)
        {
            var others = sel.Where(kv => kv.Key != col).ToDictionary(kv => kv.Key, kv => kv.Value);
            var othersRanges = ranges?.Where(kv => kv.Key != col).ToDictionary(kv => kv.Key, kv => kv.Value);
            var (where, ps) = BuildWhere(others, search, othersRanges);
            var list = new List<(object?, long)>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT {Expr(col)} AS v, COUNT(*) FROM photos {where} GROUP BY v ORDER BY v IS NULL, v";
            Bind(cmd, ps);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.IsDBNull(0) ? null : r.GetValue(0), r.GetInt64(1)));
            res[col] = list;
        }
        return res;
    }

    public static (string Where, List<(string Name, object Value)> Params) BuildWhere(
        IReadOnlyDictionary<string, List<object?>> selections, string? search = null,
        IReadOnlyDictionary<string, (object Low, object High)>? ranges = null)
    {
        var clauses = new List<string>();
        var ps = new List<(string, object)>();
        int n = 0;

        // search box: every word must match the path (folders + file name), camera, lens or tags
        if (!string.IsNullOrWhiteSpace(search))
        {
            foreach (var token in search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string pn = $"$q{n++}";
                string like = "%" + token.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                ps.Add((pn, like));
                clauses.Add($"(path LIKE {pn} ESCAPE '\\' OR camera LIKE {pn} ESCAPE '\\' OR camname(camera) LIKE {pn} ESCAPE '\\' OR lens LIKE {pn} ESCAPE '\\' OR tags LIKE {pn} ESCAPE '\\')");
            }
        }

        foreach (var (col, vals) in selections)
        {
            if (vals.Count == 0 || Array.IndexOf(FacetColumns, col) < 0) continue;
            string expr = Expr(col);
            var parts = new List<string>();
            var names = new List<string>();
            foreach (var v in vals)
            {
                if (v is null) { parts.Add($"{expr} IS NULL"); continue; }
                string pn = $"$p{n++}";
                ps.Add((pn, v));
                if (col == "iso") parts.Add($"(iso >= {pn} AND iso < {pn} + 100)");
                else names.Add(pn);
            }
            if (names.Count > 0) parts.Add($"{expr} IN ({string.Join(",", names)})");
            clauses.Add("(" + string.Join(" OR ", parts) + ")");
        }

        // range sliders: low <= value <= high (on the same expression the filter list groups by)
        if (ranges != null)
        {
            foreach (var (col, r) in ranges)
            {
                if (Array.IndexOf(FacetColumns, col) < 0) continue;
                string a = $"$p{n++}", b = $"$p{n++}";
                ps.Add((a, r.Low));
                ps.Add((b, r.High));
                clauses.Add($"{Expr(col)} BETWEEN {a} AND {b}");
            }
        }
        return (clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses), ps);
    }

    private static void Bind(SqliteCommand cmd, List<(string Name, object Value)> ps)
    {
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
    }

    public int CountFiltered(string where, List<(string Name, object Value)> ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM photos {where}";
        Bind(cmd, ps);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<PhotoRecord> QueryPage(string where, List<(string Name, object Value)> ps,
        string sortCol, bool desc, int limit, int offset, int seed = 0)
    {
        string[] allowed = { "taken", "focal", "aperture", "shutter", "iso", "random" };
        if (Array.IndexOf(allowed, sortCol) < 0) sortCol = "taken";
        string dir = desc ? "DESC" : "ASC";

        string order;
        if (sortCol == "random")
        {
            order = $"((id + {seed}) * 2654435761) % 4294967296, id";
        }
        else
        {
            string e = sortCol == "focal" ? "NULLIF(fequiv(COALESCE(focal,-1.0), camera), -1.0)" : sortCol;
            order = $"{e} IS NULL, {e} {dir}, taken {dir}, id";
        }

        var list = new List<PhotoRecord>(limit);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM photos {where} ORDER BY {order} LIMIT $lim OFFSET $off";
        Bind(cmd, ps);
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$off", offset);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PhotoRecord
            {
                Id = r.GetInt64(0), Path = r.GetString(1), Name = r.GetString(2),
                Size = r.GetInt64(3), MTime = r.GetInt64(4), Taken = r.GetString(5),
                Year = I(r, 6), Month = I(r, 7), Day = I(r, 8), Hour = I(r, 9),
                Camera = r.GetString(10), Lens = r.GetString(11),
                Focal = D(r, 12), Aperture = D(r, 13), Shutter = D(r, 14),
                Iso = I(r, 15), Width = I(r, 16), Height = I(r, 17),
                Tags = r.GetString(18)
            });
        }
        return list;
    }

    private static int? I(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
}
