using ContiExif.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Xmp;

namespace ContiExif.Services;

public static class ExifReader
{
    private static string Clean(string? s) => (s ?? "").Trim().Trim('\0').Trim();

    public static PhotoRecord Read(FileInfo fi)
    {
        var r = new PhotoRecord
        {
            Path = fi.FullName,
            Name = fi.Name,
            Size = fi.Length,
            MTime = fi.LastWriteTimeUtc.Ticks
        };
        DateTime? taken = null;
        try
        {
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
            var dirs = ImageMetadataReader.ReadMetadata(fs);
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();

            string make = Clean(ifd0?.GetString(ExifDirectoryBase.TagMake));
            string model = Clean(ifd0?.GetString(ExifDirectoryBase.TagModel));
            r.Camera = model.Length == 0
                ? make
                : (make.Length > 0 && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                    ? $"{make} {model}" : model);
            r.Lens = Clean(sub?.GetString(ExifDirectoryBase.TagLensModel));

            if (sub != null)
            {
                if (sub.TryGetRational(ExifDirectoryBase.TagFocalLength, out var fl))
                {
                    double v = Math.Round(fl.ToDouble(), 1);
                    if (v > 0) r.Focal = v;
                }
                if (sub.TryGetRational(ExifDirectoryBase.TagFNumber, out var fn))
                {
                    double v = Math.Round(fn.ToDouble(), 1);
                    if (v > 0) r.Aperture = v;
                }
                if (sub.TryGetRational(ExifDirectoryBase.TagExposureTime, out var et))
                {
                    double v = Math.Round(et.ToDouble(), 8);
                    if (v > 0) r.Shutter = v;
                }
                if (sub.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out int iso) && iso > 0) r.Iso = iso;
                if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt)) taken = dt;
                if (sub.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out int w)) r.Width = w;
                if (sub.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out int h)) r.Height = h;
            }
            if (taken == null && ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt0))
                taken = dt0;
            if (r.Width == null)
            {
                var jpg = dirs.OfType<JpegDirectory>().FirstOrDefault();
                if (jpg != null)
                {
                    if (jpg.TryGetInt32(JpegDirectory.TagImageWidth, out int w)) r.Width = w;
                    if (jpg.TryGetInt32(JpegDirectory.TagImageHeight, out int h)) r.Height = h;
                }
            }

            // Tags (Windows Properties > Details > Tags): EXIF XPKeywords and XMP dc:subject
            var tags = new List<string>();
            string? win = ifd0?.GetDescription(ExifDirectoryBase.TagWinKeywords);
            if (!string.IsNullOrWhiteSpace(win))
                tags.AddRange(win.Split(new[] { ';', '\0' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            foreach (var xmp in dirs.OfType<XmpDirectory>())
            {
                foreach (var kv in xmp.GetXmpProperties())
                {
                    if (kv.Key.StartsWith("dc:subject", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(kv.Value))
                        tags.Add(kv.Value.Trim());
                }
            }
            r.Tags = string.Join("; ", tags.Distinct(StringComparer.OrdinalIgnoreCase));
        }
        catch { }

        var t = taken ?? fi.LastWriteTime;
        r.Taken = t.ToString("yyyy-MM-dd HH:mm:ss");
        r.Year = t.Year; r.Month = t.Month; r.Day = t.Day; r.Hour = t.Hour;
        return r;
    }
}
