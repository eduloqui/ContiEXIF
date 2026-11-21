using System.Globalization;
using System.Text;

namespace ContiExif.Services;

public sealed class TiffEdits
{
    public string? Model { get; init; }
    public string? Lens { get; init; }
    public double? Focal { get; init; }
    public double? Aperture { get; init; }
    public DateTime Taken { get; init; }
}

/// <summary>Lossless EXIF patcher for JPEG: only the APP1 Exif block is rewritten, image data is untouched.</summary>
public static class ExifWriter
{
    public static byte[] PatchJpeg(byte[] jpg, TiffEdits edits)
    {
        if (jpg.Length < 4 || jpg[0] != 0xFF || jpg[1] != 0xD8)
            throw new InvalidDataException("Not a JPEG file.");

        int exifStart = -1, exifEnd = -1, insertAt = 2;
        int i = 2;
        while (i + 4 <= jpg.Length && jpg[i] == 0xFF)
        {
            byte m = jpg[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m == 0xDA || m == 0xD9) break;
            if (m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
            int len = (jpg[i + 2] << 8) | jpg[i + 3];
            int end = i + 2 + len;
            if (end > jpg.Length) break;
            if (m == 0xE1 && len >= 8 && exifStart < 0 &&
                jpg[i + 4] == (byte)'E' && jpg[i + 5] == (byte)'x' && jpg[i + 6] == (byte)'i' &&
                jpg[i + 7] == (byte)'f' && jpg[i + 8] == 0 && jpg[i + 9] == 0)
            {
                exifStart = i; exifEnd = end;
            }
            if (m == 0xE0 && i == 2) insertAt = end;
            i = end;
        }

        byte[]? tiff = exifStart >= 0
            ? jpg.AsSpan(exifStart + 10, exifEnd - exifStart - 10).ToArray()
            : null;
        byte[] newTiff = new TiffPatcher(tiff).Apply(edits);

        int segLen = 2 + 6 + newTiff.Length;
        if (segLen > 65535) throw new InvalidDataException("EXIF block too large.");
        var app1 = new byte[2 + segLen];
        app1[0] = 0xFF; app1[1] = 0xE1;
        app1[2] = (byte)(segLen >> 8); app1[3] = (byte)segLen;
        app1[4] = (byte)'E'; app1[5] = (byte)'x'; app1[6] = (byte)'i'; app1[7] = (byte)'f';
        Array.Copy(newTiff, 0, app1, 10, newTiff.Length);

        using var ms = new MemoryStream(jpg.Length + app1.Length);
        if (exifStart >= 0)
        {
            ms.Write(jpg, 0, exifStart);
            ms.Write(app1, 0, app1.Length);
            ms.Write(jpg, exifEnd, jpg.Length - exifEnd);
        }
        else
        {
            ms.Write(jpg, 0, insertAt);
            ms.Write(app1, 0, app1.Length);
            ms.Write(jpg, insertAt, jpg.Length - insertAt);
        }
        return ms.ToArray();
    }
}

internal sealed class TiffPatcher
{
    private sealed class Entry
    {
        public ushort Tag, Type;
        public uint Count;
        public byte[] Raw = new byte[4];
    }

    private readonly List<byte> _b;
    private readonly bool _le;

    public TiffPatcher(byte[]? tiff)
    {
        if (tiff != null && tiff.Length >= 8 &&
            ((tiff[0] == 'I' && tiff[1] == 'I') || (tiff[0] == 'M' && tiff[1] == 'M')))
        {
            _b = new List<byte>(tiff);
            _le = tiff[0] == 'I';
        }
        else
        {
            _le = true;
            _b = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        }
    }

    public byte[] Apply(TiffEdits e)
    {
        var ifd0 = ReadIfd(R32(4), out uint next0);

        var exif = new List<Entry>();
        uint nextE = 0;
        var ptr = ifd0.FirstOrDefault(x => x.Tag == 0x8769);
        if (ptr != null) exif = ReadIfd(U32(ptr.Raw), out nextE);

        if (e.Model != null)
        {
            Set(ifd0, 0x0110, 2, Ascii(e.Model));   // Model
            Remove(ifd0, 0x010F);                   // Make (camera is stored as a single name)
        }
        string dt = e.Taken.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
        Set(ifd0, 0x0132, 2, Ascii(dt));            // DateTime
        Set(exif, 0x9003, 2, Ascii(dt));            // DateTimeOriginal
        Set(exif, 0x9004, 2, Ascii(dt));            // DateTimeDigitized
        if (e.Lens != null)
        {
            Set(exif, 0xA434, 2, Ascii(e.Lens));    // LensModel
            Remove(exif, 0xA433);                   // LensMake
        }
        if (e.Focal is double f)
            Set(exif, 0x920A, 5, Rational(Math.Round(f * 10), 10));
        if (e.Aperture is double a)
        {
            Set(exif, 0x829D, 5, Rational(Math.Round(a * 10), 10));
            Set(exif, 0x9202, 5, Rational(Math.Max(0, Math.Round(2 * Math.Log2(a) * 100)), 100));
        }

        uint exifOff = WriteIfd(exif, nextE);
        Set(ifd0, 0x8769, 4, B32(exifOff));
        uint ifd0Off = WriteIfd(ifd0, next0);
        var hdr = B32(ifd0Off);
        for (int k = 0; k < 4; k++) _b[4 + k] = hdr[k];
        return _b.ToArray();
    }

    // ---------- IFD helpers ----------
    private List<Entry> ReadIfd(uint off, out uint next)
    {
        if (off < 8 || off + 2 > _b.Count) throw new InvalidDataException("Bad IFD offset.");
        int n = R16((int)off);
        long end = off + 2 + n * 12L + 4;
        if (end > _b.Count) throw new InvalidDataException("Bad IFD.");
        var list = new List<Entry>(n);
        for (int k = 0; k < n; k++)
        {
            int o = (int)off + 2 + k * 12;
            var en = new Entry { Tag = R16(o), Type = R16(o + 2), Count = R32(o + 4) };
            for (int j = 0; j < 4; j++) en.Raw[j] = _b[o + 8 + j];
            list.Add(en);
        }
        next = R32((int)off + 2 + n * 12);
        return list;
    }

    private uint WriteIfd(List<Entry> list, uint next)
    {
        list.Sort((x, y) => x.Tag.CompareTo(y.Tag));
        if (_b.Count % 2 == 1) _b.Add(0);
        uint off = (uint)_b.Count;
        _b.AddRange(B16((ushort)list.Count));
        foreach (var en in list)
        {
            _b.AddRange(B16(en.Tag));
            _b.AddRange(B16(en.Type));
            _b.AddRange(B32(en.Count));
            _b.AddRange(en.Raw);
        }
        _b.AddRange(B32(next));
        return off;
    }

    private static void Remove(List<Entry> list, ushort tag) => list.RemoveAll(x => x.Tag == tag);

    private void Set(List<Entry> list, ushort tag, ushort type, byte[] data)
    {
        var en = list.FirstOrDefault(x => x.Tag == tag);
        long oldSize = 0;
        uint oldOff = 0;
        if (en == null) { en = new Entry { Tag = tag }; list.Add(en); }
        else
        {
            oldSize = (long)TypeSize(en.Type) * en.Count;
            if (oldSize > 4) oldOff = U32(en.Raw);
        }
        en.Type = type;
        en.Count = (uint)(data.Length / TypeSize(type));

        if (data.Length <= 4)
        {
            var raw = new byte[4];
            Array.Copy(data, raw, data.Length);
            en.Raw = raw;
            return;
        }
        if (oldSize > 4 && oldSize >= data.Length && oldOff + oldSize <= _b.Count)
        {
            for (int k = 0; k < oldSize; k++) _b[(int)oldOff + k] = k < data.Length ? data[k] : (byte)0;
            en.Raw = B32(oldOff);
            return;
        }
        if (_b.Count % 2 == 1) _b.Add(0);
        uint off = (uint)_b.Count;
        _b.AddRange(data);
        en.Raw = B32(off);
    }

    private static int TypeSize(ushort t) => t switch
    {
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 1
    };

    // ---------- encoding ----------
    private static byte[] Ascii(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var r = new byte[bytes.Length + 1];
        Array.Copy(bytes, r, bytes.Length);
        return r;
    }

    private byte[] Rational(double num, uint den)
    {
        var r = new byte[8];
        Array.Copy(B32((uint)num), 0, r, 0, 4);
        Array.Copy(B32(den), 0, r, 4, 4);
        return r;
    }

    private ushort R16(int o) => _le ? (ushort)(_b[o] | _b[o + 1] << 8) : (ushort)(_b[o] << 8 | _b[o + 1]);

    private uint R32(int o) => _le
        ? (uint)(_b[o] | _b[o + 1] << 8 | _b[o + 2] << 16 | _b[o + 3] << 24)
        : (uint)(_b[o] << 24 | _b[o + 1] << 16 | _b[o + 2] << 8 | _b[o + 3]);

    private uint U32(byte[] a) => _le
        ? (uint)(a[0] | a[1] << 8 | a[2] << 16 | a[3] << 24)
        : (uint)(a[0] << 24 | a[1] << 16 | a[2] << 8 | a[3]);

    private byte[] B16(ushort v) => _le
        ? new[] { (byte)v, (byte)(v >> 8) }
        : new[] { (byte)(v >> 8), (byte)v };

    private byte[] B32(uint v) => _le
        ? new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }
        : new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
}
