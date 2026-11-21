using System.Collections.Concurrent;
using ContiExif.Models;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace ContiExif.Services;

public static class ThumbnailService
{
    public const int W = 360, H = 240;
    private const float Quality = 0.7f;

    private static readonly SemaphoreSlim Gate = new(3);
    private static readonly ConcurrentDictionary<string, byte> Failed = new();

    public static string Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    public static string Key(string owner, string path, long mtime, long size)
        => Hash($"{owner}|{path}|{mtime}|{size}");

    /// <summary>Thumbnail for a tile: stored one if present, otherwise generated now and stored.</summary>
    public static async Task<byte[]?> GetOrCreateAsync(string owner, PhotoRecord r, CancellationToken ct)
    {
        string key = Key(owner, r.Path, r.MTime, r.Size);
        var store = ThumbStore.Instance;
        var bytes = await Task.Run(() => store.Get(key), ct);
        if (bytes != null) return bytes;
        if (Failed.ContainsKey(key)) return null;

        await Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            bytes = await Task.Run(() =>
            {
                var existing = store.Get(key);
                if (existing != null) return existing;
                var made = Generate(r.Path);
                if (made != null) store.PutMany(new[] { (key, owner, made) });
                return made;
            }, ct);
            if (bytes == null) Failed[key] = 0;
            return bytes;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Renders a 360x240 (3:2, cover-cropped) JPEG at quality ~70. Blocking; call from a background thread.</summary>
    public static byte[]? Generate(string path)
    {
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            using var thumb = file.GetThumbnailAsync(ThumbnailMode.PicturesView, 720, ThumbnailOptions.ResizeThumbnail)
                .AsTask().GetAwaiter().GetResult();
            if (thumb == null || thumb.Size == 0 || thumb.Type != ThumbnailType.Image) return null;

            var dec = BitmapDecoder.CreateAsync(thumb).AsTask().GetAwaiter().GetResult();
            double w = dec.PixelWidth, h = dec.PixelHeight;
            if (w < 1 || h < 1) return null;
            double s = Math.Max(W / w, H / h);
            uint sw = (uint)Math.Max(W, Math.Ceiling(w * s));
            uint sh = (uint)Math.Max(H, Math.Ceiling(h * s));
            var transform = new BitmapTransform
            {
                ScaledWidth = sw,
                ScaledHeight = sh,
                InterpolationMode = BitmapInterpolationMode.Fant,
                Bounds = new BitmapBounds { X = (sw - W) / 2, Y = (sh - H) / 2, Width = W, Height = H }
            };
            var pixels = dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
                .AsTask().GetAwaiter().GetResult();

            using var ms = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(Quality, PropertyType.Single) }
            };
            var enc = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms, options).AsTask().GetAwaiter().GetResult();
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, W, H, 96, 96, pixels.DetachPixelData());
            enc.FlushAsync().AsTask().GetAwaiter().GetResult();

            var bytes = new byte[ms.Size];
            using var reader = new DataReader(ms.GetInputStreamAt(0));
            reader.LoadAsync((uint)ms.Size).AsTask().GetAwaiter().GetResult();
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    /// <summary>Removes thumbnails of this folder's database that no longer match any photo, and reclaims the space.</summary>
    public static void Cleanup(Database db, string owner)
    {
        var idx = db.LoadIndex();
        var keep = new HashSet<string>(idx.Select(kv => Key(owner, kv.Key, kv.Value.MTime, kv.Value.Size)));
        ThumbStore.Instance.Purge(owner, keep);
    }
}
