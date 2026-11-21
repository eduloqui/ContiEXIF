using System.Globalization;
using ContiExif.Models;

namespace ContiExif.Services;

public sealed record EditRequest(string? Camera, string? Lens, double? Focal, double? Aperture, int HourShift);
public sealed record EditResult(int Done, int Skipped, List<string> Errors);

public static class PhotoEditor
{
    public static EditResult Apply(IReadOnlyList<PhotoRecord> photos, EditRequest req, Database db, IProgress<string> progress)
    {
        int done = 0, skipped = 0;
        var errors = new List<string>();
        Directory.CreateDirectory(AppPaths.Originals);

        for (int idx = 0; idx < photos.Count; idx++)
        {
            var p = photos[idx];
            progress.Report($"Editing {idx + 1:N0} / {photos.Count:N0}…");

            string ext = Path.GetExtension(p.Path);
            if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            try
            {
                var taken = DateTime.ParseExact(p.Taken, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    .AddHours(req.HourShift);
                string dir = Path.GetDirectoryName(p.Path)!;
                string target = UniqueTarget(dir, taken, p.Path);

                // patch in memory first (no side effects if it fails)
                byte[] data = File.ReadAllBytes(p.Path);
                byte[] patched = ExifWriter.PatchJpeg(data, new TiffEdits
                {
                    Model = req.Camera,
                    Lens = req.Lens,
                    Focal = req.Focal,
                    Aperture = req.Aperture,
                    Taken = taken
                });

                // backup the original
                File.Copy(p.Path, UniqueBackup(p.Name), false);

                // write + swap
                string tmp = Path.Combine(dir, ".contiexif-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllBytes(tmp, patched);
                if (string.Equals(target, p.Path, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(tmp, p.Path, true);
                    if (!string.Equals(target, p.Path, StringComparison.Ordinal)) { }
                }
                else
                {
                    File.Move(tmp, target);
                    File.Delete(p.Path);
                }
                File.SetLastWriteTime(target, taken);

                var rec = ExifReader.Read(new FileInfo(target));
                db.Delete(new[] { p.Path });
                db.Upsert(new[] { rec });
                done++;
            }
            catch (Exception ex)
            {
                errors.Add($"{p.Name}: {ex.Message}");
            }
        }
        return new EditResult(done, skipped, errors);
    }

    private static string UniqueTarget(string dir, DateTime t, string original)
    {
        string baseName = t.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        for (int i = 1; ; i++)
        {
            string name = i == 1 ? baseName + ".jpg" : $"{baseName}_{i}.jpg";
            string path = Path.Combine(dir, name);
            if (string.Equals(path, original, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return path;
        }
    }

    private static string UniqueBackup(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 0; ; i++)
        {
            string path = Path.Combine(AppPaths.Originals, i == 0 ? name : $"{stem}_{i}{ext}");
            if (!File.Exists(path)) return path;
        }
    }
}
