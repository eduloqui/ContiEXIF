using System.Threading.Channels;
using ContiExif.Models;

namespace ContiExif.Services;

public sealed record ScanProgress(string Message);

public static class Scanner
{
    private static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".heic", ".gif" };

    public static Task RunAsync(string root, Database db, string owner,
        IProgress<ScanProgress> progress, CancellationToken ct)
        => Task.Run(async () =>
        {
            progress.Report(new("Enumerating files…"));
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var found = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (!Exts.Contains(fi.Extension)) continue;
                found[fi.FullName] = fi;
                if (++n % 1000 == 0) progress.Report(new($"Enumerating files… {n:N0}"));
            }

            var index = db.LoadIndex();
            var todo = new List<FileInfo>();
            foreach (var (path, fi) in found)
            {
                if (!index.TryGetValue(path, out var e) || e.Outdated ||
                    e.Size != fi.Length || e.MTime != fi.LastWriteTimeUtc.Ticks)
                    todo.Add(fi);
            }
            var deleted = index.Keys.Where(k => !found.ContainsKey(k)).ToList();
            db.Delete(deleted);

            int total = todo.Count, done = 0;
            progress.Report(new($"{found.Count:N0} files found · {total:N0} to read · {deleted.Count:N0} removed"));

            var channel = Channel.CreateBounded<PhotoRecord>(1000);
            var writer = Task.Run(async () =>
            {
                var batch = new List<PhotoRecord>(500);
                await foreach (var rec in channel.Reader.ReadAllAsync(ct))
                {
                    batch.Add(rec);
                    if (batch.Count >= 500) { db.Upsert(batch); batch.Clear(); }
                }
                if (batch.Count > 0) db.Upsert(batch);
            }, ct);

            try
            {
                await Parallel.ForEachAsync(todo, new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
                },
                async (fi, token) =>
                {
                    var rec = ExifReader.Read(fi);
                    await channel.Writer.WriteAsync(rec, token);
                    int d = Interlocked.Increment(ref done);
                    if (d % 50 == 0 || d == total)
                        progress.Report(new($"Scanning… {d:N0} / {total:N0}"));
                });
            }
            finally { channel.Writer.Complete(); }
            await writer;

            ThumbnailService.Cleanup(db, owner);
        }, ct);
}
