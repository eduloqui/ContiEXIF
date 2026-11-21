using System.Text;
using ContiExif.Models;
using ContiExif.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ContiExif.Views;

public sealed partial class DetailPopup : UserControl
{
    public event Action? OpenFileRequested;
    public event Action? ShowFolderRequested;
    public event Action? CloseRequested;

    public DetailPopup() => InitializeComponent();

    public void Show(PhotoItem item, bool pinned)
    {
        var r = item.Record;
        NameText.Text = r.Name;
        PathText.Text = r.Path;
        DetailsText.Text = Build(r);
        Preview.Source = item.Thumb;
        Actions.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = pinned;
    }

    private static string Build(PhotoRecord r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Taken:  {r.Taken}");
        if (r.Camera.Length > 0)
        {
            string cam = CameraProfiles.DisplayName(r.Camera);
            sb.AppendLine(cam == r.Camera ? $"Camera:  {cam}" : $"Camera:  {cam}  ({r.Camera})");
        }
        if (r.Lens.Length > 0) sb.AppendLine($"Lens:  {r.Lens}");
        var exp = new List<string>();
        if (r.Focal is double f)
        {
            double m = CameraProfiles.Multiplier(r.Camera);
            exp.Add(m == 1 ? $"{f:0.#} mm" : $"{f:0.#} mm ({Math.Round(f * m):0} mm eq.)");
        }
        if (r.Aperture is double a) exp.Add(Formatters.Aperture(a));
        if (r.Shutter is double s) exp.Add(Formatters.Shutter(s));
        if (r.Iso is int iso) exp.Add($"ISO {iso}");
        if (exp.Count > 0) sb.AppendLine(string.Join("  ·  ", exp));
        if (r.Tags.Length > 0) sb.AppendLine($"Tags:  {r.Tags}");
        if (r.Width is int w && r.Height is int h) sb.Append($"{w} × {h} px  ·  ");
        sb.Append(Formatters.Size(r.Size));
        return sb.ToString();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFileRequested?.Invoke();
    private void ShowFolder_Click(object sender, RoutedEventArgs e) => ShowFolderRequested?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
