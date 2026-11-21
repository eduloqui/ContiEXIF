using System.Collections.ObjectModel;
using ContiExif.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ContiExif.Models;

public sealed class FilterOption : NotifyBase
{
    private static readonly HashSet<long> KeyFocals = new() { 10, 15, 16, 18, 24, 35, 50, 55, 85, 135, 250 };

    private bool _selected;
    private long _count;

    public FilterOption(FilterGroup group, object? value, string label, long count)
    {
        Group = group; Value = value; Label = label; _count = count;
        bool key = group.Key == "focal" && value is not null
                   && KeyFocals.Contains((long)Math.Round(Convert.ToDouble(value)));
        LabelWeight = key ? FontWeights.Bold : FontWeights.Normal;
    }

    public FilterGroup Group { get; }
    public object? Value { get; }
    public string Label { get; }
    public Windows.UI.Text.FontWeight LabelWeight { get; }
    public long Count => _count;
    public string CountText => _count.ToString("N0");
    public double RowOpacity => _count == 0 ? 0.5 : 1;

    public void SetCount(long c)
    {
        if (_count == c) return;
        _count = c;
        Raise(nameof(Count)); Raise(nameof(CountText)); Raise(nameof(RowOpacity));
    }

    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public void OnClick(object sender, RoutedEventArgs e) => Group.Toggle(this);
}

public sealed class FilterGroup : NotifyBase
{
    private readonly List<FilterOption> _all = new();
    private bool _expanded;
    private bool _isRange;
    private object? _rangeLow, _rangeHigh;

    public FilterGroup(string key, string title)
    {
        Key = key;
        Title = title;
        IconSource = LoadIcon(key);
    }

    public string Key { get; }
    public string Title { get; }
    public ObservableCollection<FilterOption> Options { get; } = new();

    /// <summary>A list selection or a range changed: the photos must be queried again.</summary>
    public event Action? Changed;
    /// <summary>The range data (values, thumbs positions) changed: sliders must be redrawn.</summary>
    public event Action? RangeUpdated;

    // ---------- icon (Assets\Icons\<key>.svg or .png) ----------
    public ImageSource? IconSource { get; }
    public Visibility IconVis => IconSource != null ? Visibility.Visible : Visibility.Collapsed;

    private static ImageSource? LoadIcon(string key)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons");
            if (File.Exists(Path.Combine(dir, key + ".svg")))
                return new SvgImageSource(new Uri($"ms-appx:///Assets/Icons/{key}.svg"));
            string png = Path.Combine(dir, key + ".png");
            if (File.Exists(png)) return new BitmapImage(new Uri(png));
        }
        catch { }
        return null;
    }

    // ---------- expander ----------
    public bool IsExpanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    public bool IsFiltering => _all.Any(o => o.IsSelected) || ActiveRange != null;

    public string HeaderText
    {
        get
        {
            if (_isRange) return ActiveRange != null ? $"{Title}  (range)" : Title;
            int n = _all.Count(o => o.IsSelected);
            return n > 0 ? $"{Title}  ({n})" : Title;
        }
    }

    public Visibility ActiveVis => IsFiltering ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseHeader() { Raise(nameof(HeaderText)); Raise(nameof(ActiveVis)); }

    // ---------- list (checkboxes) ----------
    public List<object?> SelectedValues() => _all.Where(o => o.IsSelected).Select(o => o.Value).ToList();

    public void Toggle(FilterOption option)
    {
        option.IsSelected = !option.IsSelected;
        RaiseHeader();
        Changed?.Invoke();
    }

    /// <summary>Syncs the visible options with the facet values (in place, no list reset).</summary>
    public void Update(IEnumerable<(object? Value, string Label, long Count)> items)
    {
        var list = items.ToList();

        var existing = new Dictionary<object, FilterOption>();
        foreach (var o in _all) existing[o.Value ?? DBNull.Value] = o;

        var next = new List<FilterOption>();
        var seen = new HashSet<object>();
        foreach (var it in list)
        {
            var k = it.Value ?? DBNull.Value;
            seen.Add(k);
            if (existing.TryGetValue(k, out var o)) o.SetCount(it.Count);
            else o = new FilterOption(this, it.Value, it.Label, it.Count);
            next.Add(o);
        }
        // keep selected values that no longer match, greyed out, so they can be unselected
        foreach (var o in _all)
        {
            if (o.IsSelected && !seen.Contains(o.Value ?? DBNull.Value)) { o.SetCount(0); next.Add(o); }
        }
        _all.Clear();
        _all.AddRange(next);
        SyncOptions();

        if (IsRangeCapable)
        {
            RangeItems = list.Where(i => i.Value != null)
                             .Select(i => (i.Value!, Formatters.RangeLabel(Key, i.Value)))
                             .ToList();
            RecalcRangeIndices();
            RangeUpdated?.Invoke();
        }
        RaiseHeader();
    }

    private void SyncOptions()
    {
        var set = new HashSet<FilterOption>(_all);
        for (int i = Options.Count - 1; i >= 0; i--)
            if (!set.Contains(Options[i])) Options.RemoveAt(i);

        for (int i = 0; i < _all.Count; i++)
        {
            if (i < Options.Count && ReferenceEquals(Options[i], _all[i])) continue;
            int idx = Options.IndexOf(_all[i]);
            if (idx >= 0) Options.Move(idx, i);
            else Options.Insert(i, _all[i]);
        }
    }

    // ---------- range slider (focal length, aperture, shutter speed, ISO) ----------
    public bool IsRangeCapable => Key is "focal" or "aperture" or "shutter" or "iso";
    public Visibility RangeToggleVis => IsRangeCapable ? Visibility.Visible : Visibility.Collapsed;

    public bool IsRange
    {
        get => _isRange;
        private set
        {
            if (!Set(ref _isRange, value)) return;
            Raise(nameof(ListVis)); Raise(nameof(RangeVis));
        }
    }
    public Visibility ListVis => _isRange ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RangeVis => _isRange ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Distinct values present right now, ascending (lower values first).</summary>
    public List<(object Value, string Label)> RangeItems { get; private set; } = new();
    public int RangeLowIndex { get; private set; }
    public int RangeHighIndex { get; private set; }

    /// <summary>The restricted range, or null when the slider is off or covers everything.</summary>
    public (object Low, object High)? ActiveRange =>
        _isRange && _rangeLow != null && _rangeHigh != null ? (_rangeLow, _rangeHigh) : null;

    public void OnRangeToggle(object sender, RoutedEventArgs e) => ToggleRange();

    public void ToggleRange()
    {
        if (!IsRangeCapable) return;
        bool on = !_isRange;
        foreach (var o in _all) o.IsSelected = false;
        _rangeLow = _rangeHigh = null;
        IsRange = on;
        RecalcRangeIndices();
        RaiseHeader();
        RangeUpdated?.Invoke();
        Changed?.Invoke();
    }

    public void SetRange(int lo, int hi)
    {
        int n = RangeItems.Count;
        if (n == 0) return;
        lo = Math.Clamp(lo, 0, n - 1);
        hi = Math.Clamp(hi, 0, n - 1);
        if (lo > hi) (lo, hi) = (hi, lo);
        bool full = lo == 0 && hi == n - 1;
        _rangeLow = full ? null : RangeItems[lo].Value;
        _rangeHigh = full ? null : RangeItems[hi].Value;
        RangeLowIndex = lo;
        RangeHighIndex = hi;
        RaiseHeader();
        RangeUpdated?.Invoke();
        Changed?.Invoke();
    }

    private void RecalcRangeIndices()
    {
        int n = RangeItems.Count;
        if (n == 0) { RangeLowIndex = RangeHighIndex = 0; return; }
        if (_rangeLow == null || _rangeHigh == null) { RangeLowIndex = 0; RangeHighIndex = n - 1; return; }

        double lo = Convert.ToDouble(_rangeLow), hi = Convert.ToDouble(_rangeHigh);
        int li = RangeItems.FindIndex(x => Convert.ToDouble(x.Value) >= lo);
        int hiIdx = RangeItems.FindLastIndex(x => Convert.ToDouble(x.Value) <= hi);
        if (li < 0) li = n - 1;
        if (hiIdx < 0) hiIdx = 0;
        if (li > hiIdx) hiIdx = li;
        RangeLowIndex = li;
        RangeHighIndex = hiIdx;
    }

    private bool RangeHasValues()
    {
        if (_rangeLow == null || _rangeHigh == null) return true;
        double lo = Convert.ToDouble(_rangeLow), hi = Convert.ToDouble(_rangeHigh);
        return RangeItems.Any(x => { double v = Convert.ToDouble(x.Value); return v >= lo && v <= hi; });
    }

    // ---------- reset / cleanup ----------
    /// <summary>Clears the selection and the range and goes back to the list.</summary>
    public void Reset()
    {
        foreach (var o in _all) o.IsSelected = false;
        _rangeLow = _rangeHigh = null;
        IsRange = false;
        RecalcRangeIndices();
        RaiseHeader();
        RangeUpdated?.Invoke();
    }

    /// <summary>Unchecks selected values (and a range) that no longer match any photo. Returns true if something changed.</summary>
    public bool UncheckEmpty()
    {
        bool changed = false;
        foreach (var o in _all)
        {
            if (o.IsSelected && o.Count == 0) { o.IsSelected = false; changed = true; }
        }
        if (_isRange && _rangeLow != null && !RangeHasValues())
        {
            _rangeLow = _rangeHigh = null;
            RecalcRangeIndices();
            RangeUpdated?.Invoke();
            changed = true;
        }
        if (changed) RaiseHeader();
        return changed;
    }
}
