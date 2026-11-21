using ContiExif.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ContiExif.Views;

/// <summary>
/// Dual-handle slider over the values a filter group currently has (lower values on the left, higher on the right).
/// It reads and writes the range of the FilterGroup in its DataContext.
/// </summary>
public sealed class RangeSlider : UserControl
{
    private const double ThumbSize = 18, Pad = 10, TrackHeight = 4, CanvasHeight = 28;

    private readonly Canvas _canvas = new()
    {
        Height = CanvasHeight,
        Background = new SolidColorBrush(Colors.Transparent)
    };
    private readonly Border _track = new()
    {
        Height = TrackHeight,
        CornerRadius = new CornerRadius(2),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x80, 0x80, 0x80)),
        IsHitTestVisible = false
    };
    private readonly Border _fill = new()
    {
        Height = TrackHeight,
        CornerRadius = new CornerRadius(2),
        IsHitTestVisible = false
    };
    private readonly Border _lowThumb;
    private readonly Border _highThumb;
    private readonly TextBlock _label = new() { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };

    private FilterGroup? _g;
    private int _lo, _hi, _count;
    private int _drag; // 0 none, 1 lower thumb, 2 upper thumb

    public RangeSlider()
    {
        var accent = (Brush)Application.Current.Resources["ContiAccentBrush"];
        _fill.Background = accent;
        _lowThumb = MakeThumb(accent);
        _highThumb = MakeThumb(accent);

        _canvas.Children.Add(_track);
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_lowThumb);
        _canvas.Children.Add(_highThumb);
        _canvas.SizeChanged += (_, _) => Layout();
        _canvas.PointerPressed += Canvas_PointerPressed;
        _canvas.PointerMoved += Canvas_PointerMoved;
        _canvas.PointerReleased += Canvas_PointerReleased;
        _canvas.PointerCaptureLost += Canvas_PointerCaptureLost;

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(_label);
        panel.Children.Add(_canvas);
        Content = panel;

        DataContextChanged += (_, e) => Attach(e.NewValue as FilterGroup);
        Loaded += (_, _) => Attach(DataContext as FilterGroup);
        Unloaded += (_, _) => Attach(null);
    }

    private static Border MakeThumb(Brush accent) => new()
    {
        Width = ThumbSize,
        Height = ThumbSize,
        CornerRadius = new CornerRadius(ThumbSize / 2),
        Background = new SolidColorBrush(Colors.White),
        BorderBrush = accent,
        BorderThickness = new Thickness(3),
        IsHitTestVisible = false
    };

    private void Attach(FilterGroup? g)
    {
        if (!ReferenceEquals(g, _g))
        {
            if (_g != null) _g.RangeUpdated -= Refresh;
            _g = g;
            if (_g != null) _g.RangeUpdated += Refresh;
        }
        Refresh();
    }

    private void Refresh()
    {
        if (_g == null || _drag != 0) return;
        _count = _g.RangeItems.Count;
        _lo = _g.RangeLowIndex;
        _hi = _g.RangeHighIndex;
        IsEnabled = _count > 1;
        Layout();
    }

    private double XOf(int i)
    {
        double w = Math.Max(0, _canvas.ActualWidth - 2 * Pad);
        return Pad + (_count <= 1 ? 0 : w * i / (_count - 1));
    }

    private int IndexAt(double x)
    {
        if (_count <= 1) return 0;
        double w = Math.Max(1, _canvas.ActualWidth - 2 * Pad);
        int i = (int)Math.Round((x - Pad) / w * (_count - 1));
        return Math.Clamp(i, 0, _count - 1);
    }

    private void Layout()
    {
        double w = _canvas.ActualWidth;
        UpdateLabel();
        if (w <= 0) return;

        double cy = CanvasHeight / 2;
        double xl = XOf(_lo), xh = XOf(_hi);

        _track.Width = Math.Max(0, w - 2 * Pad);
        Canvas.SetLeft(_track, Pad);
        Canvas.SetTop(_track, cy - TrackHeight / 2);

        _fill.Width = Math.Max(0, xh - xl);
        Canvas.SetLeft(_fill, xl);
        Canvas.SetTop(_fill, cy - TrackHeight / 2);

        Canvas.SetLeft(_lowThumb, xl - ThumbSize / 2);
        Canvas.SetTop(_lowThumb, cy - ThumbSize / 2);
        Canvas.SetLeft(_highThumb, xh - ThumbSize / 2);
        Canvas.SetTop(_highThumb, cy - ThumbSize / 2);
    }

    private void UpdateLabel()
    {
        if (_g == null || _count == 0) { _label.Text = "No values"; return; }
        var items = _g.RangeItems;
        string a = items[Math.Clamp(_lo, 0, _count - 1)].Label;
        string b = items[Math.Clamp(_hi, 0, _count - 1)].Label;
        _label.Text = _lo == _hi ? a : $"{a}  –  {b}";
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_g == null || _count <= 1) return;
        int idx = IndexAt(e.GetCurrentPoint(_canvas).Position.X);

        if (_lo == _hi) _drag = idx < _lo ? 1 : idx > _hi ? 2 : (_lo == 0 ? 2 : 1);
        else _drag = Math.Abs(idx - _lo) <= Math.Abs(idx - _hi) ? 1 : 2;

        _canvas.CapturePointer(e.Pointer);
        Move(idx);
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == 0) return;
        Move(IndexAt(e.GetCurrentPoint(_canvas).Position.X));
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == 0) return;
        _canvas.ReleasePointerCapture(e.Pointer);
        Commit();
    }

    private void Canvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => Commit();

    private void Move(int idx)
    {
        if (_drag == 1) _lo = Math.Min(idx, _hi);
        else if (_drag == 2) _hi = Math.Max(idx, _lo);
        Layout();
    }

    private void Commit()
    {
        if (_drag == 0) return;
        _drag = 0;
        _g?.SetRange(_lo, _hi);
    }
}
