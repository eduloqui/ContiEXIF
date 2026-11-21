using Microsoft.Data.Sqlite;

namespace ContiExif.Services;

/// <summary>Single SQLite file holding every thumbnail (JPEG blobs) for all scanned folders.</summary>
public sealed class ThumbStore
{
    private static readonly Lazy<ThumbStore> _instance = new(() => new ThumbStore());
    public static ThumbStore Instance => _instance.Value;

    private readonly string _cs;
    private readonly object _writeLock = new();

    private ThumbStore()
    {
        Directory.CreateDirectory(AppPaths.Root);
        bool isNew = !File.Exists(AppPaths.ThumbsDb);
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.ThumbsDb,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();

        using var c = Open();
        if (isNew) Exec(c, "PRAGMA auto_vacuum=INCREMENTAL;");
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, @"CREATE TABLE IF NOT EXISTS thumbs(key TEXT PRIMARY KEY, owner TEXT NOT NULL, data BLOB NOT NULL);
                  CREATE INDEX IF NOT EXISTS ix_thumbs_owner ON thumbs(owner);");
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA synchronous=NORMAL;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public byte[]? Get(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM thumbs WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as byte[];
    }

    public bool Has(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM thumbs WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() != null;
    }

    public HashSet<string> KeysFor(string owner)
    {
        var set = new HashSet<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key FROM thumbs WHERE owner=$o";
        cmd.Parameters.AddWithValue("$o", owner);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public void PutMany(IEnumerable<(string Key, string Owner, byte[] Data)> items)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO thumbs(key,owner,data) VALUES($k,$o,$d)";
            var pk = cmd.CreateParameter(); pk.ParameterName = "$k"; cmd.Parameters.Add(pk);
            var po = cmd.CreateParameter(); po.ParameterName = "$o"; cmd.Parameters.Add(po);
            var pd = cmd.CreateParameter(); pd.ParameterName = "$d"; cmd.Parameters.Add(pd);
            foreach (var it in items)
            {
                pk.Value = it.Key; po.Value = it.Owner; pd.Value = it.Data;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Deletes this owner's thumbnails that are not in <paramref name="keep"/> and returns the freed space.</summary>
    public void Purge(string owner, HashSet<string> keep)
    {
        var stale = KeysFor(owner).Where(k => !keep.Contains(k)).ToList();
        if (stale.Count == 0) return;
        lock (_writeLock)
        {
            using var c = Open();
            using (var tx = c.BeginTransaction())
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM thumbs WHERE key=$k";
                var p = cmd.CreateParameter(); p.ParameterName = "$k"; cmd.Parameters.Add(p);
                foreach (var k in stale) { p.Value = k; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
            Exec(c, "PRAGMA incremental_vacuum;");
        }
    }
}
