using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using ContiExif.Models;
using ContiExif.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ContiExif.ViewModels;

public sealed class MainViewModel : NotifyBase
{
    private static readonly string[] SortCols = { "taken", "focal", "aperture", "shutter", "iso", "random" };
    private static readonly int[] PageSizes = { 60, 120, 240, 480 };
    private const int RandomIndex = 5;

    public string[] SortItems { get; } = { "Date", "Focal length", "Aperture", "Shutter speed", "ISO", "Random" };
    public string[] PageSizeItems { get; } = { "60", "120", "240", "480" };
    public ObservableCollection<FilterGroup> Groups { get; } = new();
    public ObservableCollection<PhotoItem> Photos { get; } = new();
    public event Action? PageReplaced;

    private AppSettings _s = new();
    private Database? _db;
    private string? _root;
    private string _owner = "";
    private int _ver;
    private bool _suppress;
    private int _seed = Random.Shared.Next(1, int.MaxValue);
    private string _search = "";
    private CancellationTokenSource? _searchCts;

    private string _folderText = "No folder selected";
    private string _message = "Scan a folder to begin.";
    private bool _busy, _sortDesc = true;
    private int _total, _filtered, _selectedCount, _pageIndex, _pageCount = 1, _sortIndex, _pageSizeIndex = 1;

    public MainViewModel()
    {
        _thumbs.Changed += (d, t, st) => _dq?.TryEnqueue(() => OnThumbs(d, t, st));

        void Add(string key, string title)
        {
            var g = new FilterGroup(key, title);
            g.Changed += () => { Raise(nameof(CanReset)); _ = RefreshAsync(true); };
            Groups.Add(g);
        }
        Add("camera", "Camera"); Add("lens", "Lens"); Add("focal", "Focal length");
        Add("aperture", "Aperture"); Add("shutter", "Shutter speed"); Add("iso", "ISO");
        Add("year", "Year"); Add("month", "Month"); Add("day", "Day"); Add("hour", "Hour");
    }

    public string FolderText { get => _folderText; private set => Set(ref _folderText, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            Raise(nameof(IsNotBusy));
            Raise(nameof(ProgressVisibility));
            Raise(nameof(SaveEnabled));
        }
    }
    public bool IsNotBusy => !_busy;
    public Visibility ProgressVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;

    public int SelectedCount
    {
        get => _selectedCount;
        set
        {
            if (!Set(ref _selectedCount, value)) return;
            Raise(nameof(StatusCounts)); Raise(nameof(SaveText));
            Raise(nameof(SaveEnabled)); Raise(nameof(EditInfo)); Raise(nameof(EditPreview));
        }
    }

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (value < 0 || !Set(ref _sortIndex, value) || _suppress) return;
            _sortDesc = value == 0;
            if (value == RandomIndex) _seed = Random.Shared.Next(1, int.MaxValue);
            Raise(nameof(SortDirText));
            _ = RefreshAsync(true);
        }
    }

    public bool SortDesc => _sortDesc;
    public string SortDirText => _sortIndex == RandomIndex ? "⟳ Reshuffle" : (_sortDesc ? "↓ Descending" : "↑ Ascending");

    public int PageSizeIndex
    {
        get => _pageSizeIndex;
        set
        {
            if (value < 0 || !Set(ref _pageSizeIndex, value) || _suppress) return;
            _ = RefreshAsync(true);
        }
    }

    /// <summary>True when any filter (list, range or search text) is active.</summary>
    public bool CanReset => !string.IsNullOrEmpty(_search) || Groups.Any(g => g.IsFiltering);

    public string StatusCounts =>
        $"{_total:N0} in folder  |  {_filtered:N0} match filters  |  {_selectedCount:N0} selected";
    public string PageText => $"Page {_pageIndex + 1:N0} / {_pageCount:N0}";
    public bool CanPrev => _pageIndex > 0;
    public bool CanNext => _pageIndex < _pageCount - 1;
    public Visibility EmptyVisibility => Photos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateStatus()
    {
        Raise(nameof(StatusCounts)); Raise(nameof(PageText));
        Raise(nameof(CanPrev)); Raise(nameof(CanNext)); Raise(nameof(EmptyVisibility));
        Raise(nameof(CanReset));
    }

    // ---------- Background thumbnails ----------
    private readonly ThumbBuilder _thumbs = new();
    private readonly DispatcherQueue? _dq = DispatcherQueue.GetForCurrentThread();
    private ThumbState _tstate = ThumbState.Idle;
    private int _tdone, _ttotal;

    public string ThumbText => _tstate == ThumbState.Paused
        ? $"Thumbnails paused {_tdone:N0} / {_ttotal:N0}"
        : $"Thumbnails {_tdone:N0} / {_ttotal:N0}";
    public string ThumbPauseText => _tstate == ThumbState.Paused ? "Resume" : "Pause";
    public Visibility ThumbVisibility => _tstate == ThumbState.Idle ? Visibility.Collapsed : Visibility.Visible;

    private void StartThumbs()
    {
        if (_db == null) return;
        _thumbs.Start(_db, _owner);
    }

    public void ToggleThumbPause() => _thumbs.TogglePause();

    private void OnThumbs(int done, int total, ThumbState st)
    {
        bool finished = st == ThumbState.Idle && _tstate != ThumbState.Idle;
        _tdone = done; _ttotal = total; _tstate = st;
        Raise(nameof(ThumbText)); Raise(nameof(ThumbPauseText)); Raise(nameof(ThumbVisibility));
        if (finished) Message = "Thumbnails ready.";
    }

    // ---------- Edit mode ----------
    private bool _editMode;
    private int _editShift;
    public List<string> Cameras { get; private set; } = new();
    public List<string> Lenses { get; private set; } = new();

    public bool EditMode
    {
        get => _editMode;
        private set
        {
            if (!Set(ref _editMode, value)) return;
            Raise(nameof(EditVisibility)); Raise(nameof(SaveEnabled));
            Raise(nameof(EditInfo)); Raise(nameof(EditPreview));
        }
    }
    public Visibility EditVisibility => _editMode ? Visibility.Visible : Visibility.Collapsed;
    public string SaveText => $"Save changes ({_selectedCount:N0})";
    public bool SaveEnabled => _editMode && _selectedCount > 0 && !_busy;
    public string EditInfo =>
        $"{_selectedCount:N0} selected on this page · empty fields stay unchanged · JPEG only · " +
        $"originals are backed up to {AppPaths.Originals} · files are renamed to YYYY-MM-DD_HH-MM-SS.jpg";

    public int EditShift
    {
        get => _editShift;
        set { if (Set(ref _editShift, value)) Raise(nameof(EditPreview)); }
    }

    /// <summary>Resulting date, time and file name of the first selected photo with the current hour shift.</summary>
    public string EditPreview
    {
        get
        {
            var sel = SelectedItems();
            if (sel.Count == 0) return "Select photos to preview the resulting date, time and file name.";
            var p = sel[0].Record;
            if (!DateTime.TryParseExact(p.Taken, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var t))
                return p.Name;
            var n = t.AddHours(_editShift);
            string text = $"Preview: {p.Taken}  →  {n.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}" +
                          $"  →  {n.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.jpg";
            return sel.Count > 1 ? text + $"   (+{sel.Count - 1:N0} more with the same shift)" : text;
        }
    }

    public void ToggleEdit() => EditMode = !EditMode;
    public void ExitEdit() => EditMode = false;

    // Edit-mode selection is tracked on the items themselves (independent of the grid's selection mode)
    public void SetAllSelected(bool value)
    {
        foreach (var p in Photos) p.IsSelected = value;
        SelectedCount = value ? Photos.Count : 0;
        Raise(nameof(EditPreview));
    }

    public void ToggleSelected(PhotoItem item)
    {
        item.IsSelected = !item.IsSelected;
        SelectedCount = Photos.Count(p => p.IsSelected);
        Raise(nameof(EditPreview));
    }

    public List<PhotoItem> SelectedItems() => Photos.Where(p => p.IsSelected).ToList();

    public async Task ApplyEditsAsync(IReadOnlyList<PhotoItem> items, EditRequest req)
    {
        if (_busy || _db == null) return;
        var recs = items.Select(i => i.Record).ToList();
        if (recs.Count == 0) return;
        var db = _db;
        Busy = true;
        try
        {
            var prog = new Progress<string>(m => Message = m);
            string owner = _owner;
            var res = await Task.Run(() =>
            {
                var r = PhotoEditor.Apply(recs, req, db, prog);
                ThumbnailService.Cleanup(db, owner);
                return r;
            });
            string msg = $"Edited {res.Done:N0} photo(s)";
            if (res.Skipped > 0) msg += $", {res.Skipped:N0} skipped (not JPEG)";
            if (res.Errors.Count > 0) msg += $", {res.Errors.Count:N0} failed ({res.Errors[0]})";
            Message = msg + ". Originals backed up.";
        }
        catch (Exception ex) { Message = "Edit failed: " + ex.Message; }
        finally { Busy = false; }
        EditMode = false;
        await ReloadAsync(true, false, true);   // filter values left with zero photos get unchecked
    }

    // ---------- Search ----------
    public async void SetSearch(string text)
    {
        text = (text ?? "").Trim();
        if (text == _search) return;
        _search = text;
        Raise(nameof(CanReset));
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, cts.Token);
            await RefreshAsync(true);
        }
        catch (OperationCanceledException) { }
    }

    // ---------- Camera profiles ----------
    public Task<List<string>> RawCamerasAsync(string display)
    {
        var db = _db;
        return db == null ? Task.FromResult(new List<string>()) : Task.Run(() => db.RawCamerasFor(display));
    }

    /// <summary>Called after a camera name or multiplier changed: names and focal lengths are recalculated.</summary>
    public async Task CameraProfilesChangedAsync()
    {
        foreach (var g in Groups.Where(x => x.Key is "camera" or "focal")) g.Reset();
        await RefreshAsync(false);
    }

    // ---------- Startup / folders ----------
    public async Task InitAsync()
    {
        _s = SettingsService.Load();
        _ = Task.Run(() =>
        {
            try { if (Directory.Exists(AppPaths.LegacyCache)) Directory.Delete(AppPaths.LegacyCache, true); } catch { }
        });
        await Task.Run(CameraProfiles.Load);

        _suppress = true;
        SortIndex = 0;          // always starts sorted by date, newest first
        _sortDesc = true;
        Raise(nameof(SortDirText));
        PageSizeIndex = Math.Clamp(_s.PageSizeIndex, 0, PageSizes.Length - 1);
        _suppress = false;

        if (!string.IsNullOrEmpty(_s.LastDb) && File.Exists(_s.LastDb))
            await OpenDbAsync(_s.LastDb);
    }

    public async Task OpenDbAsync(string path)
    {
        if (_busy) return;
        try
        {
            Message = "Loading…";
            var db = await Task.Run(() => new Database(path));
            _db = db;
            _root = await Task.Run(() => db.Root);
            _owner = Path.GetFileNameWithoutExtension(path);
            FolderText = _root ?? _owner;
            await ReloadAsync(false, true);
            _s.LastDb = path;
            SettingsService.Save(_s);
            Message = "Ready.";

            // databases from older versions: re-read EXIF once (decimal focal lengths, tags)
            if (_root != null && Directory.Exists(_root) && await Task.Run(() => db.CountOutdated()) > 0)
            {
                Message = "Updating EXIF data (focal lengths, tags)…";
                await RunScanAsync(true);
            }
        }
        catch (Exception ex) { Message = "Failed to open database: " + ex.Message; }
    }

    public async Task ScanFolderAsync(string folder)
    {
        if (_busy) return;
        string full = Path.GetFullPath(folder);
        if (full.Length > 3) full = full.TrimEnd('\\', '/');
        try
        {
            string dbPath = await Task.Run(() => AppPaths.DbFor(full));
            _db = await Task.Run(() => { var d = new Database(dbPath); d.SetRoot(full); return d; });
            _root = full;
            _owner = Path.GetFileNameWithoutExtension(dbPath);
            FolderText = full;
            _s.LastDb = dbPath;
            SettingsService.Save(_s);
            await RunScanAsync(false);
        }
        catch (Exception ex) { Message = "Scan failed: " + ex.Message; }
    }

    public async Task RescanAsync()
    {
        if (_busy) return;
        if (_db == null || _root == null) { Message = "No folder to rescan."; return; }
        if (!Directory.Exists(_root)) { Message = "Folder not found: " + _root; return; }
        await RunScanAsync(true);
    }

    private async Task RunScanAsync(bool keep)
    {
        Busy = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var prog = new Progress<ScanProgress>(p => Message = p.Message);
            await Scanner.RunAsync(_root!, _db!, _owner, prog, CancellationToken.None);
            Message = $"Scan complete in {sw.Elapsed.TotalSeconds:0.#}s.";
        }
        catch (Exception ex) { Message = "Scan failed: " + ex.Message; }
        finally { Busy = false; }
        await ReloadAsync(keep, !keep);
    }

    private async Task ReloadAsync(bool keepSelection, bool resetPage, bool dropEmpty = false)
    {
        var db = _db!;
        var (total, cams, lenses) = await Task.Run(() => (db.CountAll(), db.Distinct("camera"), db.Distinct("lens")));
        _total = total; Cameras = cams; Lenses = lenses;
        if (!keepSelection) foreach (var g in Groups) g.Reset();
        await RefreshAsync(resetPage, dropEmpty);
        StartThumbs();
    }

    public async Task RefreshAsync(bool resetPage, bool dropEmpty = false)
    {
        var db = _db;
        if (db == null) return;
        int ver = ++_ver;
        var sel = Groups.ToDictionary(g => g.Key, g => g.SelectedValues());
        string search = _search;
        var ranges = new Dictionary<string, (object Low, object High)>();
        foreach (var g in Groups)
        {
            if (g.ActiveRange is { } r) ranges[g.Key] = r;
        }
        var (where, ps) = Database.BuildWhere(sel, search, ranges);
        int size = PageSizes[Math.Clamp(_pageSizeIndex, 0, PageSizes.Length - 1)];
        string col = SortCols[Math.Clamp(_sortIndex, 0, SortCols.Length - 1)];
        bool desc = _sortDesc;
        int seed = _seed;
        int want = resetPage ? 0 : _pageIndex;

        var (count, pageIdx, recs, facets) = await Task.Run(() =>
        {
            int c = db.CountFiltered(where, ps);
            int pc = Math.Max(1, (c + size - 1) / size);
            int pi = Math.Min(want, pc - 1);
            var page = db.QueryPage(where, ps, col, desc, size, pi * size, seed);
            return (c, pi, page, db.LoadFacets(sel, search, ranges));
        });
        if (ver != _ver) return;

        foreach (var g in Groups)
            g.Update(facets[g.Key].Select(f => (f.Value, Formatters.Label(g.Key, f.Value), f.Count)));

        if (dropEmpty)
        {
            bool any = false;
            foreach (var g in Groups) any |= g.UncheckEmpty();
            if (any)
            {
                await RefreshAsync(resetPage);
                return;
            }
        }

        _filtered = count;
        _pageCount = Math.Max(1, (count + size - 1) / size);
        _pageIndex = pageIdx;
        Photos.Clear();
        foreach (var r in recs) Photos.Add(new PhotoItem(r, _owner));
        PageReplaced?.Invoke();
        UpdateStatus();
    }

    public async Task GoPageAsync(int index)
    {
        index = Math.Clamp(index, 0, _pageCount - 1);
        if (index == _pageIndex) return;
        _pageIndex = index;
        await RefreshAsync(false);
    }

    public Task FirstPage() => GoPageAsync(0);
    public Task PrevPage() => GoPageAsync(_pageIndex - 1);
    public Task NextPage() => GoPageAsync(_pageIndex + 1);
    public Task LastPage() => GoPageAsync(_pageCount - 1);

    public void ResetFilters()
    {
        _search = "";
        _searchCts?.Cancel();
        foreach (var g in Groups) { g.Reset(); g.IsExpanded = false; }
        Raise(nameof(CanReset));
        _ = RefreshAsync(true);
    }

    public void ToggleSortDir()
    {
        if (_sortIndex == RandomIndex) _seed = Random.Shared.Next(1, int.MaxValue);
        else _sortDesc = !_sortDesc;
        Raise(nameof(SortDirText));
        _ = RefreshAsync(true);
    }

    public void SaveSettings()
    {
        _s.PageSizeIndex = _pageSizeIndex;
        SettingsService.Save(_s);
    }
}
