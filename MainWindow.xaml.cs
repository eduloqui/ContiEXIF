using System.Diagnostics;
using ContiExif.Models;
using ContiExif.Services;
using ContiExif.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ContiExif;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    private PhotoItem? _pinnedItem;
    private bool _pinned;
    private Rect _popupRect;

    public MainWindow()
    {
        InitializeComponent();

        var v = typeof(App).Assembly.GetName().Version;
        string ver = v == null ? "" : v.Build > 0 ? $" {v.Major}.{v.Minor}.{v.Build}" : $" {v.Major}.{v.Minor}";
        Title = "ContiExif" + ver;
        TitleText.Text = "ContiExif" + ver;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 920));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();   // opens maximized
        string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "ContiExif.ico");
        if (File.Exists(ico)) AppWindow.SetIcon(ico);

        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), true);

        Detail.OpenFileRequested += () => OpenFile(_pinnedItem);
        Detail.ShowFolderRequested += () => ShowFolder(_pinnedItem);
        Detail.CloseRequested += Unpin;
        ViewModel.PageReplaced += Unpin;

        Root.Loaded += async (_, _) =>
        {
            DetailHost.XamlRoot = Root.XamlRoot;
            await ViewModel.InitAsync();
        };
        Closed += (_, _) => ViewModel.SaveSettings();
    }

    // ---------- Sidebar buttons ----------
    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var p = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeWithWindow.Initialize(p, WindowNative.GetWindowHandle(this));
        p.FileTypeFilter.Add("*");
        var f = await p.PickSingleFolderAsync();
        if (f != null) await ViewModel.ScanFolderAsync(f.Path);
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ViewModel.RescanAsync();

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetFilters();
        SearchBox.Text = "";
    }

    private void ThumbPause_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleThumbPause();
    private void SortDir_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleSortDir();

    private async void First_Click(object sender, RoutedEventArgs e) => await ViewModel.FirstPage();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await ViewModel.PrevPage();
    private async void Next_Click(object sender, RoutedEventArgs e) => await ViewModel.NextPage();
    private async void Last_Click(object sender, RoutedEventArgs e) => await ViewModel.LastPage();

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ViewModel.SetSearch(SearchBox.Text);

    // ---------- Camera: right-click -> custom name + focal multiplier ----------
    private void Option_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not FilterOption opt) return;
        if (opt.Group.Key != "camera") return;

        var flyout = new MenuFlyout();
        var item = new MenuFlyoutItem { Text = "Name and focal multiplier…", Icon = new FontIcon { Glyph = "\uE70F" } };
        item.Click += (_, _) => EditCamera(opt);
        flyout.Items.Add(item);
        flyout.ShowAt(fe, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(fe) });
        e.Handled = true;
    }

    private async void EditCamera(FilterOption opt)
    {
        string display = opt.Value as string ?? "";
        if (display.Length == 0) return;
        var raws = await ViewModel.RawCamerasAsync(display);
        if (raws.Count == 0) return;
        var existing = raws.Select(CameraProfiles.Get).FirstOrDefault(p => p != null);

        var nameBox = new TextBox { Header = "Custom name", Text = display };
        var multBox = new NumberBox
        {
            Header = "Focal multiplier (full-frame equivalent)",
            PlaceholderText = "None",
            Value = existing?.Multiplier ?? double.NaN,
            Minimum = 0.1,
            Maximum = 50,
            SmallChange = 0.1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = "Original: " + string.Join(", ", raws),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        });
        panel.Children.Add(nameBox);
        panel.Children.Add(multBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Example: 5.575 turns a 4 mm photo into 22 mm in the focal length list. Leave empty for no conversion. Photo files are not modified.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7
        });

        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Camera",
            Content = panel,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Remove custom settings",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var res = await dlg.ShowAsync();

        if (res == ContentDialogResult.Primary)
        {
            string? name = nameBox.Text.Trim();
            if (name.Length == 0 || (raws.Count == 1 && string.Equals(name, raws[0], StringComparison.OrdinalIgnoreCase)))
                name = null;
            double? mult = double.IsNaN(multBox.Value) || multBox.Value <= 0 ? null : multBox.Value;
            await Task.Run(() => CameraProfiles.Save(raws, name, mult));
        }
        else if (res == ContentDialogResult.Secondary)
        {
            await Task.Run(() => CameraProfiles.Remove(raws));
        }
        else return;

        await ViewModel.CameraProfilesChangedAsync();
    }

    // ---------- Grid ----------
    private void Grid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) { (args.Item as PhotoItem)?.CancelThumb(); return; }
        (args.Item as PhotoItem)?.LoadThumb();
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.EditMode) return;
        foreach (var i in e.AddedItems.OfType<PhotoItem>()) i.IsSelected = true;
        foreach (var i in e.RemovedItems.OfType<PhotoItem>()) i.IsSelected = false;
        ViewModel.SelectedCount = PhotoGrid.SelectedItems.Count;
    }

    // ---------- Edit mode ----------
    private void EditToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleEdit();
        ApplyEditModeUi();
    }

    private void EditCancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExitEdit();
        ApplyEditModeUi();
    }

    private void ApplyEditModeUi()
    {
        Unpin();
        ViewModel.SetAllSelected(false);
        if (PhotoGrid.SelectionMode != ListViewSelectionMode.None)
            PhotoGrid.SelectedItems.Clear();
        PhotoGrid.SelectionMode = ViewModel.EditMode ? ListViewSelectionMode.None : ListViewSelectionMode.Extended;
        if (!ViewModel.EditMode)
        {
            CameraBox.Text = "";
            LensBox.Text = "";
            FocalBox.Value = double.NaN;
            ApertureBox.Value = double.NaN;
            ShiftBox.Value = 0;
        }
    }

    private void SelectPage_Click(object sender, RoutedEventArgs e) => ViewModel.SetAllSelected(true);
    private void ClearSel_Click(object sender, RoutedEventArgs e) => ViewModel.SetAllSelected(false);

    private void ShiftBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => ViewModel.EditShift = double.IsNaN(args.NewValue) ? 0 : (int)Math.Round(args.NewValue);

    private void AutoBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var src = ReferenceEquals(sender, CameraBox) ? ViewModel.Cameras : ViewModel.Lenses;
        string t = sender.Text.Trim();
        sender.ItemsSource = src
            .Where(x => t.Length == 0 || x.Contains(t, StringComparison.OrdinalIgnoreCase))
            .Take(30).ToList();
    }

    private void AutoBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        => sender.Text = args.SelectedItem?.ToString() ?? "";

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var items = ViewModel.SelectedItems();
        if (items.Count == 0) return;

        string? cam = string.IsNullOrWhiteSpace(CameraBox.Text) ? null : CameraBox.Text.Trim();
        string? lens = string.IsNullOrWhiteSpace(LensBox.Text) ? null : LensBox.Text.Trim();
        double? focal = double.IsNaN(FocalBox.Value) ? null : FocalBox.Value;
        double? aperture = double.IsNaN(ApertureBox.Value) ? null : ApertureBox.Value;
        int shift = double.IsNaN(ShiftBox.Value) ? 0 : (int)Math.Round(ShiftBox.Value);
        var req = new EditRequest(cam, lens, focal, aperture, shift);

        var dlg = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Save changes?",
            Content = $"{items.Count:N0} photo(s) will be modified and renamed to YYYY-MM-DD_HH-MM-SS.jpg.\n" +
                      $"Originals are backed up to:\n{AppPaths.Originals}",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        await ViewModel.ApplyEditsAsync(items, req);
        ApplyEditModeUi();
    }

    // ---------- Click popup ----------
    private void Tile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var tile = (FrameworkElement)sender;
        if (tile.DataContext is not PhotoItem item) return;
        if (ViewModel.EditMode) { ViewModel.ToggleSelected(item); return; }
        _pinned = true;
        _pinnedItem = item;
        Detail.Show(item, true);
        DetailHost.IsOpen = true;
        Place(tile);
    }

    private void Tile_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.EditMode) return;
        OpenFile(((FrameworkElement)sender).DataContext as PhotoItem);
    }

    private void Place(FrameworkElement tile)
    {
        var pt = tile.TransformToVisual(Root).TransformPoint(new Point(0, 0));
        Detail.Measure(new Size(400, double.PositiveInfinity));
        var size = Detail.DesiredSize;
        double x = pt.X + tile.ActualWidth + 8;
        if (x + size.Width > Root.ActualWidth - 8) x = pt.X - size.Width - 8;
        x = Math.Max(8, x);
        double y = pt.Y;
        if (y + size.Height > Root.ActualHeight - 8) y = Root.ActualHeight - size.Height - 8;
        y = Math.Max(8, y);
        DetailHost.HorizontalOffset = x;
        DetailHost.VerticalOffset = y;
        _popupRect = new Rect(x, y, size.Width, size.Height);
    }

    private void Unpin()
    {
        _pinned = false;
        _pinnedItem = null;
        DetailHost.IsOpen = false;
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_pinned) return;
        var p = e.GetCurrentPoint(Root).Position;
        if (!_popupRect.Contains(p)) Unpin();
    }

    private void Esc_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Unpin();
        args.Handled = true;
    }

    // ---------- Drag out ----------
    private async void Tile_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (ViewModel.EditMode) { args.Cancel = true; return; }
        var deferral = args.GetDeferral();
        try
        {
            var item = ((FrameworkElement)sender).DataContext as PhotoItem;
            var items = item != null && item.IsSelected
                ? PhotoGrid.SelectedItems.OfType<PhotoItem>().ToList()
                : item != null ? new List<PhotoItem> { item } : new List<PhotoItem>();
            var files = new List<IStorageItem>();
            foreach (var i in items)
            {
                try { files.Add(await StorageFile.GetFileFromPathAsync(i.Record.Path)); } catch { }
            }
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.Data.SetStorageItems(files);
        }
        finally { deferral.Complete(); }
    }

    // ---------- Shell actions ----------
    private static void OpenFile(PhotoItem? item)
    {
        if (item == null) return;
        try { Process.Start(new ProcessStartInfo(item.Record.Path) { UseShellExecute = true }); } catch { }
    }

    private static void ShowFolder(PhotoItem? item)
    {
        if (item == null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Record.Path}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
