namespace ContiExif.Models;

public sealed class AppSettings
{
    public string? LastDb { get; set; }
    public bool MultiMode { get; set; }
    public int SortIndex { get; set; }
    public bool SortDesc { get; set; } = true;
    public int PageSizeIndex { get; set; } = 1;
}
