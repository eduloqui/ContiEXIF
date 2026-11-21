namespace ContiExif.Models;

public sealed class PhotoRecord
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long MTime { get; set; }
    public string Taken { get; set; } = "";
    public int? Year { get; set; }
    public int? Month { get; set; }
    public int? Day { get; set; }
    public int? Hour { get; set; }
    public string Camera { get; set; } = "";
    public string Lens { get; set; } = "";
    public double? Focal { get; set; }
    public double? Aperture { get; set; }
    public double? Shutter { get; set; }
    public int? Iso { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string Tags { get; set; } = "";
}
