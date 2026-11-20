using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PlatzAtlas.Core;

namespace PlatzAtlas.Studio;

public partial class PixelEditorWindow
{
    private readonly List<EditableFrame> _frames;
    private readonly DispatcherTimer _previewTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    private int _activeFrame;
    private bool _syncTimeline;
    private long _previewStart;
    private PixelClip? _previewClip;
    private BitmapSource[]? _previewImages;
    private double _canvasZoom = 1;
    private (Point Start, double X, double Y)? _canvasPan;

    private void InitializeAnimation()
    {
        _previewTimer.Tick += (_, _) => PreviewFrame();
        Closed += (_, _) => _previewTimer.Stop();
        PixelScroll.SizeChanged += (_, _) => ResizeView();
        PixelScroll.LostMouseCapture += (_, _) => _canvasPan = null;
        RefreshTimeline();
    }

    private void RefreshTimeline()
    {
        _syncTimeline = true;
        TimelineFrames.ItemsSource = _frames.Select((frame, index) => $"Frame {index + 1} · {frame.Duration} ms").ToArray();
        TimelineFrames.SelectedIndex = _activeFrame;
        FrameDuration.Text = _frames[_activeFrame].Duration.ToString(CultureInfo.InvariantCulture);
        var count = _frames.Count;
        AddFrame.IsEnabled = DuplicateFrame.IsEnabled = count < PixelClip.MaxFrames &&
            (count + 1) * _draft.Size * _draft.Size <= PixelAsset.MaxProjectPixels;
        DeleteFrame.IsEnabled = TimelinePlay.IsEnabled = ExportSpriteSheet.IsEnabled = count > 1;
        FrameEarlier.IsEnabled = _activeFrame > 0;
        FrameLater.IsEnabled = _activeFrame < count - 1;
        CanvasSizeNote.Text = count == 1 ? $"{_draft.Size} × {_draft.Size} pixels" :
            $"{_draft.Size} × {_draft.Size} pixels · Resize single-frame sprites.";
        _syncTimeline = false;
    }

    private void FrameSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncTimeline || TimelineFrames.SelectedIndex < 0)
            return;
        StopPreview();
        FinishStroke();
        _activeFrame = TimelineFrames.SelectedIndex;
        RefreshTimeline();
        RefreshPixels();
    }

    private void TimelineEdited(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
            return;
        StopPreview();
        FinishStroke();
        switch (action)
        {
            case "blank" when AddFrame.IsEnabled:
            case "duplicate" when DuplicateFrame.IsEnabled:
                var current = _frames[_activeFrame];
                var size = _draft.Size;
                var content = action == "blank" ? null : new PixelAsset(size, size, _draft.CopyRgba());
                _frames.Insert(_activeFrame + 1, new EditableFrame(new PixelDraft(size, content), current.Duration));
                _activeFrame++;
                break;
            case "delete" when _frames.Count > 1:
                _frames.RemoveAt(_activeFrame);
                _activeFrame = Math.Min(_activeFrame, _frames.Count - 1);
                break;
            case "earlier" when FrameEarlier.IsEnabled:
            case "later" when FrameLater.IsEnabled:
                var adjacent = _activeFrame + (action == "earlier" ? -1 : 1);
                (_frames[adjacent], _frames[_activeFrame]) = (_frames[_activeFrame], _frames[adjacent]);
                _activeFrame = adjacent;
                break;
            case "duration":
                if (!int.TryParse(FrameDuration.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms is < 40 or > 5000)
                {
                    PixelStatus.Text = "Frame duration must be 40–5000 milliseconds.";
                    return;
                }
                _frames[_activeFrame] = _frames[_activeFrame] with { Duration = ms };
                break;
        }
        RefreshTimeline();
        RefreshPixels();
    }

    private PixelClip FreezeClip() => new(_draft.Size, _draft.Size, _frames.Select(frame =>
        new PixelFrame(frame.Duration, new PixelAsset(frame.Drawing.Size, frame.Drawing.Size, frame.Drawing.CopyRgba()))).ToArray());

    internal ProjectAsset CreateAsset(string name)
    {
        var clip = FreezeClip();
        return clip.Frames.Count > 1
            ? PixelClip.Create(Guid.NewGuid(), name, clip.Frames)
            : PixelAsset.Create(Guid.NewGuid(), name, clip.Width, clip.Height, clip.Frames[0].Image.Rgba.Span);
    }

    private void TimelinePlayClicked(object sender, RoutedEventArgs e)
    {
        if (_previewTimer.IsEnabled)
        {
            StopPreview();
            return;
        }
        FinishStroke();
        _previewClip = FreezeClip();
        _previewImages = _frames.Select(frame => BitmapFor(frame.Drawing)).ToArray();
        _previewStart = Stopwatch.GetTimestamp();
        TimelinePlay.Content = "Pause preview";
        PreviewFrame();
        _previewTimer.Start();
    }

    private void PreviewFrame()
    {
        if (_previewClip is not null && _previewImages is not null)
            PixelImage.Source = _previewImages[_previewClip.FrameAt(Stopwatch.GetElapsedTime(_previewStart).TotalMilliseconds)];
    }

    private void StopPreview()
    {
        if (!_previewTimer.IsEnabled)
            return;
        _previewTimer.Stop();
        _previewImages = null;
        _previewClip = null;
        TimelinePlay.Content = "Play frames";
        PixelImage.Source = _editBitmap;
    }

    private static BitmapSource BitmapFor(PixelDraft draft)
    {
        var bytes = new byte[draft.Size * draft.Size * 4];
        draft.CopyBgraTo(bytes);
        return BitmapSource.Create(draft.Size, draft.Size, 96, 96, PixelFormats.Bgra32, null, bytes, draft.Size * 4);
    }

    private void ExportSheetClicked(object sender, RoutedEventArgs e)
    {
        StopPreview();
        FinishStroke();
        var dialog = new SaveFileDialog
        {
            Title = "Export PNG sprite sheet and animation metadata",
            Filter = "Sprite sheet and metadata (*.zip)|*.zip", AddExtension = true, DefaultExt = ".zip"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            WriteSheet(dialog.FileName);
            PixelStatus.Text = $"Exported {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PixelStatus.Text = $"Sprite sheet export failed. {error.Message}";
        }
    }

    internal void WriteSheet(string path)
    {
        var clip = FreezeClip();
        var columns = Math.Min(4, clip.Frames.Count);
        var rows = (clip.Frames.Count + columns - 1) / columns;
        var bitmap = new WriteableBitmap(clip.Width * columns, clip.Height * rows, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[clip.Width * clip.Height * 4];
        var frameRecords = new List<SheetFrame>();
        for (var index = 0; index < _frames.Count; index++)
        {
            var frame = _frames[index];
            var x = index % columns * clip.Width;
            var y = index / columns * clip.Height;
            frame.Drawing.CopyBgraTo(pixels);
            bitmap.WritePixels(new Int32Rect(x, y, clip.Width, clip.Height), pixels, clip.Width * 4, 0);
            frameRecords.Add(new SheetFrame(x, y, clip.Width, clip.Height, frame.Duration));
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var pngBytes = new MemoryStream();
                encoder.Save(pngBytes);
                using (var png = archive.CreateEntry("sprites.png").Open())
                    pngBytes.WriteTo(png);
                using var metadata = archive.CreateEntry("animation.json").Open();
                JsonSerializer.Serialize(metadata, new { version = 1, image = "sprites.png", loop = true, frames = frameRecords },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void PixelZoomClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action })
        {
            _canvasZoom = action == "fit" ? 1 : Math.Clamp(_canvasZoom * (action == "in" ? 1.25 : .8), 1, 8);
            ResizeView();
        }
    }

    private void PixelZoomWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        _canvasZoom = Math.Clamp(_canvasZoom * (e.Delta > 0 ? 1.25 : .8), 1, 8);
        ResizeView();
        e.Handled = true;
    }

    private void ResizeView()
    {
        var size = Math.Max(90, Math.Min(PixelScroll.ActualWidth, PixelScroll.ActualHeight) - 26) * _canvasZoom;
        CanvasBox.Width = CanvasBox.Height = size;
    }

    private void PixelPanDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _last is not null || !PixelScroll.CaptureMouse())
            return;
        _canvasPan = (e.GetPosition(PixelScroll), PixelScroll.HorizontalOffset, PixelScroll.VerticalOffset);
        e.Handled = true;
    }

    private void PixelPanMove(object sender, MouseEventArgs e)
    {
        if (_canvasPan is not { } pan)
            return;
        var delta = e.GetPosition(PixelScroll) - pan.Start;
        PixelScroll.ScrollToHorizontalOffset(pan.X - delta.X);
        PixelScroll.ScrollToVerticalOffset(pan.Y - delta.Y);
    }

    private void PixelPanUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _canvasPan is null)
            return;
        _canvasPan = null;
        PixelScroll.ReleaseMouseCapture();
        e.Handled = true;
    }

    private sealed record EditableFrame(PixelDraft Drawing, int Duration);
    private sealed record SheetFrame(int X, int Y, int Width, int Height, int DurationMs);
}
