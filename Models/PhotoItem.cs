using System.Runtime.InteropServices.WindowsRuntime;
using ContiExif.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace ContiExif.Models;

public sealed class PhotoItem : NotifyBase
{
    private CancellationTokenSource? _cts;
    private ImageSource? _thumb;
    private bool _selected;

    public PhotoItem(PhotoRecord record, string owner)
    {
        Record = record;
        Owner = owner;
    }

    public PhotoRecord Record { get; }
    public string Owner { get; }

    public ImageSource? Thumb
    {
        get => _thumb;
        private set => Set(ref _thumb, value);
    }

    public double SelectedOpacity => _selected ? 1 : 0;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Raise();
            Raise(nameof(SelectedOpacity));
        }
    }

    public async void LoadThumb()
    {
        if (_thumb != null || _cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var bytes = await ThumbnailService.GetOrCreateAsync(Owner, Record, cts.Token);
            if (bytes != null && !cts.IsCancellationRequested)
            {
                var bmp = new BitmapImage();
                using var ms = new InMemoryRandomAccessStream();
                await ms.WriteAsync(bytes.AsBuffer());
                ms.Seek(0);
                await bmp.SetSourceAsync(ms);
                Thumb = bmp;
            }
        }
        catch { }
        finally
        {
            if (_thumb == null) _cts = null;
        }
    }

    public void CancelThumb()
    {
        _cts?.Cancel();
        if (_thumb == null) _cts = null;
    }
}
