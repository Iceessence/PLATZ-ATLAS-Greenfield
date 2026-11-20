using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PlatzAtlas.Core;

namespace PlatzAtlas.Studio;

public partial class PixelEditorWindow : Window
{
    private static readonly uint[] Colors =
    [
        0x192331ff, 0x334b64ff, 0x6585a1ff, 0xc1d6dcff, 0xf7f4eaff, 0x11151cff,
        0x654260ff, 0xab5788ff, 0xdd847fff, 0xf0ba83ff, 0xffdc8fff, 0xd77643ff,
        0x245349ff, 0x338678ff, 0x51b785ff, 0xaed58fff, 0x78b5eaff, 0x2ce8f5ff,
        0x262b44ff, 0x3a4466ff, 0x7a367bff, 0xfeae34ff, 0x3e8948ff, 0xffffff80
    ];
    private PixelDraft _draft => _frames[_activeFrame].Drawing;
    private WriteableBitmap? _editBitmap;
    private byte[] _bgra = [];
    private Point? _last;
    private (int StartX, int StartY, int EndX, int EndY)? _shape;
    private int _x, _y;
    private Tool _tool = Tool.Paint;
    private uint _strokeColor;
    private bool _syncColor;

    internal PixelEditorWindow(ProjectAsset? source)
    {
        var clip = source is null ? null : PixelClip.Read(source);
        int[] sizes = [16, 32, 64, 128, 256];
        var side = clip is null ? 32 : Math.Min(sizes.First(size => size >= Math.Max(clip.Width, clip.Height)),
            sizes.Last(size => size * size * clip.Frames.Count <= PixelAsset.MaxProjectPixels));
        _frames = clip is null ? [new(new PixelDraft(side), PixelClip.DefaultDuration)] :
            clip.Frames.Select(frame => new EditableFrame(new PixelDraft(side, frame.Image), frame.DurationMs)).ToList();
        InitializeComponent();
        SourceNote.Text = source is null ? "Draw a new sprite" : $"Editing a copy of {source.Name}";
        AssetName.Text = source is null ? "New sprite" : (source.Name + " copy")[..Math.Min(160, source.Name.Length + 5)];
        SelectColor(Colors[9]);
        foreach (var color in Colors)
        {
            var swatch = new Button { Background = BrushFor(color), Padding = new Thickness(0), MinHeight = 26, Margin = new Thickness(2) };
            AutomationProperties.SetName(swatch, $"Color {color:X8}");
            swatch.Click += (_, _) => SelectColor(color);
            Palette.Children.Add(swatch);
        }
        InitializeAnimation();
        RefreshPixels();
    }

    public ProjectAsset? Result { get; private set; }

    private void PixelDown(object sender, MouseButtonEventArgs e)
    {
        PixelSurface.Focus();
        e.Handled = BeginPointerStroke(e.GetPosition(PixelSurface));
    }

    internal bool BeginPointerStroke(Point point)
    {
        StopPreview();
        if (_last is not null || Cell(point) is not { } cell || !StartAt((int)cell.X, (int)cell.Y))
            return false;
        _last = cell;
        if (!PixelSurface.CaptureMouse())
        {
            FinishStroke();
            RefreshPixels();
        }
        return true;
    }

    private void PixelMove(object sender, MouseEventArgs e) => ContinuePointerStroke(e.GetPosition(PixelSurface));

    internal void ContinuePointerStroke(Point point)
    {
        if (_last is not { } prior || Cell(point) is not { } next || next == prior)
            return;
        if (_shape is { } shape)
        {
            _shape = shape with { EndX = (int)next.X, EndY = (int)next.Y };
            _last = next;
            _x = (int)next.X; _y = (int)next.Y;
            RefreshPixels();
            return;
        }
        _draft.PaintLine((int)prior.X, (int)prior.Y, (int)next.X, (int)next.Y, _strokeColor);
        _last = next;
        _x = (int)next.X; _y = (int)next.Y;
        RefreshPixels();
    }

    private void PixelUp(object sender, MouseButtonEventArgs e)
    {
        PixelMove(sender, e);
        FinishStroke();
        RefreshPixels();
    }

    private void PixelCaptureLost(object sender, MouseEventArgs e)
    {
        if (_last is null)
            return;
        FinishStroke();
        RefreshPixels();
    }

    private bool StartAt(int x, int y)
    {
        _x = x; _y = y;
        if (_tool == Tool.Eyedropper)
        {
            SelectColor(_draft.Read(x, y));
            SetTool(Tool.Paint);
            RefreshPixels();
            return false;
        }
        if (_tool != Tool.Erase && !TryReadColor(out _strokeColor))
            return false;
        if (_tool == Tool.Fill)
        {
            _draft.Fill(x, y, _strokeColor);
            RefreshPixels();
            return false;
        }
        if (_tool is Tool.Line or Tool.Rect)
        {
            _shape = (x, y, x, y);
            RefreshPixels();
            return true;
        }
        if (_tool == Tool.Erase)
            _strokeColor = 0;
        _draft.BeginStroke();
        _draft.PaintLine(x, y, x, y, _strokeColor);
        RefreshPixels();
        return true;
    }

    private void FinishStroke()
    {
        _last = null;
        if (_shape is { } shape)
        {
            _shape = null;
            _draft.BeginStroke();
            CommitShape(shape.StartX, shape.StartY, shape.EndX, shape.EndY, _tool == Tool.Rect, _strokeColor);
            _draft.EndStroke();
        }
        else
            _draft.EndStroke();
        if (PixelSurface.IsMouseCaptured)
            PixelSurface.ReleaseMouseCapture();
    }

    private void CommitShape(int x0, int y0, int x1, int y1, bool rect, uint color)
    {
        if (!rect)
        {
            _draft.PaintLine(x0, y0, x1, y1, color);
            return;
        }
        var minX = Math.Min(x0, x1);
        var maxX = Math.Max(x0, x1);
        var minY = Math.Min(y0, y1);
        var maxY = Math.Max(y0, y1);
        _draft.PaintLine(minX, minY, maxX, minY, color);
        _draft.PaintLine(minX, maxY, maxX, maxY, color);
        _draft.PaintLine(minX, minY, minX, maxY, color);
        _draft.PaintLine(maxX, minY, maxX, maxY, color);
    }

    private void SizeClicked(object sender, RoutedEventArgs e)
    {
        StopPreview();
        FinishStroke();
        if (_frames.Count == 1 && sender is Button { Tag: string value } && int.TryParse(value, out var size))
            _draft.Resize(size);
        RefreshTimeline();
        RefreshPixels();
    }

    private void ToolClicked(object sender, RoutedEventArgs e)
    {
        FinishStroke();
        if (sender is Button { Tag: string value } && Enum.TryParse<Tool>(value, out var tool))
            SetTool(tool);
        RefreshPixels();
    }

    private void UndoClicked(object sender, RoutedEventArgs e)
    {
        StopPreview();
        FinishStroke();
        if (_frames.Count == 1 || _draft.CanUndoWithoutResize)
            _draft.Undo();
        RefreshTimeline();
        RefreshPixels();
    }

    private void RedoClicked(object sender, RoutedEventArgs e)
    {
        StopPreview();
        FinishStroke();
        if (_frames.Count == 1 || _draft.CanRedoWithoutResize)
            _draft.Redo();
        RefreshTimeline();
        RefreshPixels();
    }

    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not TextBox && e.Key is Key.Z or Key.Y)
        {
            if (e.Key == Key.Z) UndoClicked(sender, e);
            else RedoClicked(sender, e);
            e.Handled = true;
            return;
        }
        if (PixelSurface.IsKeyboardFocusWithin)
            e.Handled = HandleCanvasKey(e.Key);
    }

    internal bool HandleCanvasKey(Key key)
    {
        StopPreview();
        switch (key)
        {
            case Key.Left: _x--; break;
            case Key.Right: _x++; break;
            case Key.Up: _y--; break;
            case Key.Down: _y++; break;
            case Key.Space:
                if (_last is null && StartAt(_x, _y))
                {
                    FinishStroke();
                    RefreshPixels();
                }
                return true;
            default: return false;
        }
        RefreshPixels();
        return true;
    }

    private void KeepClicked(object sender, RoutedEventArgs e)
    {
        StopPreview();
        FinishStroke();
        var name = AssetName.Text.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            PixelStatus.Text = "Enter a printable asset name.";
            AssetName.Focus();
            return;
        }
        try
        {
            Result = CreateAsset(name);
            DialogResult = true;
        }
        catch (InvalidDataException error) { PixelStatus.Text = error.Message; }
    }

    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        FinishStroke();
        StopPreview();
        RefreshPixels();
        var name = AssetName.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = "Sprite";
        var dialog = new SaveFileDialog { Title = "Export sprite PNG", Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png", AddExtension = true, FileName = name + ".png" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            WritePng(dialog.FileName);
            PixelStatus.Text = $"Exported {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PixelStatus.Text = $"PNG export failed. {error.Message}";
        }
    }

    internal void WritePng(string path)
    {
        using var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateBitmap()));
        encoder.Save(png);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                png.WriteTo(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void SelectColor(uint color)
    {
        _syncColor = true;
        PixelColor.Text = $"{color:X8}";
        ApplyRgbaToSliders(color);
        _syncColor = false;
    }

    private void PixelColorChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncColor || SliderR is null)
            return;
        var value = PixelColor.Text.Trim().TrimStart('#');
        if (value.Length == 8 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color))
        {
            _syncColor = true;
            ApplyRgbaToSliders(color);
            _syncColor = false;
        }
        else if (ColorPreviewSwatch is not null)
            ColorPreviewSwatch.Background = Brushes.Transparent;
    }

    private void RgbaSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncColor || !IsLoaded || PixelColor is null)
            return;
        var rgba = ((uint)(byte)SliderR.Value << 24) | ((uint)(byte)SliderG.Value << 16) |
                   ((uint)(byte)SliderB.Value << 8) | (byte)SliderA.Value;
        _syncColor = true;
        ApplyRgbaToSliders(rgba);
        PixelColor.Text = $"{rgba:X8}";
        _syncColor = false;
    }

    private void ApplyRgbaToSliders(uint rgba)
    {
        if (SliderR is null)
            return;
        byte r = (byte)(rgba >> 24), g = (byte)(rgba >> 16), b = (byte)(rgba >> 8), a = (byte)rgba;
        SliderR.Value = r; SliderG.Value = g; SliderB.Value = b; SliderA.Value = a;
        LabelR.Text = r.ToString(CultureInfo.InvariantCulture);
        LabelG.Text = g.ToString(CultureInfo.InvariantCulture);
        LabelB.Text = b.ToString(CultureInfo.InvariantCulture);
        LabelA.Text = a.ToString(CultureInfo.InvariantCulture);
        ColorPreviewSwatch.Background = BrushFor(rgba);
    }

    private bool TryReadColor(out uint color)
    {
        var value = PixelColor.Text.Trim().TrimStart('#');
        if (value.Length == 8 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out color))
            return true;
        color = 0;
        PixelStatus.Text = "Use eight hex digits for color, for example 78B5EA80.";
        return false;
    }

    private void SetTool(Tool tool) => _tool = tool;

    private Point? Cell(Point point)
    {
        var cell = 384.0 / _draft.Size;
        return point.X is >= 0 and < 384 && point.Y is >= 0 and < 384
            ? new Point((int)(point.X / cell), (int)(point.Y / cell)) : null;
    }

    private void RefreshPixels()
    {
        _x = Math.Clamp(_x, 0, _draft.Size - 1);
        _y = Math.Clamp(_y, 0, _draft.Size - 1);
        var unit = 384.0 / _draft.Size;
        Canvas.SetLeft(PixelCursor, _x * unit);
        Canvas.SetTop(PixelCursor, _y * unit);
        PixelCursor.Width = PixelCursor.Height = unit;
        PixelCursor.Visibility = Visibility.Visible;
        if (_editBitmap?.PixelWidth != _draft.Size)
        {
            _bgra = new byte[_draft.Size * _draft.Size * 4];
            _editBitmap = new WriteableBitmap(_draft.Size, _draft.Size, 96, 96, PixelFormats.Bgra32, null);
        }
        _draft.CopyBgraTo(_bgra);
        if (_shape is { } shape)
            OverlayShapePreview(_bgra, _draft.Size, shape.StartX, shape.StartY, shape.EndX, shape.EndY, _tool == Tool.Rect, _strokeColor);
        _editBitmap.WritePixels(new Int32Rect(0, 0, _draft.Size, _draft.Size), _bgra, _draft.Size * 4, 0);
        PixelImage.Source = _editBitmap;
        PixelUndo.IsEnabled = _frames.Count == 1 ? _draft.CanUndo : _draft.CanUndoWithoutResize;
        PixelRedo.IsEnabled = _frames.Count == 1 ? _draft.CanRedo : _draft.CanRedoWithoutResize;
        foreach (var size in CanvasSizes.Children.OfType<Button>())
            size.IsEnabled = _frames.Count == 1 && !Equals(size.Tag, _draft.Size.ToString(CultureInfo.InvariantCulture));
        foreach (var button in ToolButtons.Children.OfType<Button>())
            button.BorderBrush = (Brush)FindResource(button.Tag.ToString() == _tool.ToString() ? "Accent" : "Border");
        PixelStatus.Text = $"{_tool} · Pixel {_x + 1}, {_y + 1}";
    }

    private static void OverlayShapePreview(byte[] bgra, int size, int x0, int y0, int x1, int y1, bool rect, uint color)
    {
        if (!rect)
        {
            PlotLine(bgra, size, x0, y0, x1, y1, color);
            return;
        }
        var minX = Math.Min(x0, x1);
        var maxX = Math.Max(x0, x1);
        var minY = Math.Min(y0, y1);
        var maxY = Math.Max(y0, y1);
        PlotLine(bgra, size, minX, minY, maxX, minY, color);
        PlotLine(bgra, size, minX, maxY, maxX, maxY, color);
        PlotLine(bgra, size, minX, minY, minX, maxY, color);
        PlotLine(bgra, size, maxX, minY, maxX, maxY, color);

        static void PlotLine(byte[] buf, int side, int ax, int ay, int bx, int by, uint rgba)
        {
            int dx = Math.Abs(bx - ax), sx = ax < bx ? 1 : -1;
            int dy = -Math.Abs(by - ay), sy = ay < by ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                if ((uint)ax < (uint)side && (uint)ay < (uint)side)
                {
                    int o = (ay * side + ax) * 4;
                    buf[o] = (byte)(rgba >> 8);
                    buf[o + 1] = (byte)(rgba >> 16);
                    buf[o + 2] = (byte)(rgba >> 24);
                    buf[o + 3] = (byte)rgba;
                }
                if (ax == bx && ay == by) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; ax += sx; }
                if (e2 <= dx) { err += dx; ay += sy; }
            }
        }
    }

    private BitmapSource CreateBitmap()
    {
        var bgra = _draft.CopyRgba();
        for (var i = 0; i < bgra.Length; i += 4)
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return BitmapSource.Create(_draft.Size, _draft.Size, 96, 96, PixelFormats.Bgra32, null, bgra, _draft.Size * 4);
    }

    private static Brush BrushFor(uint rgba) => new SolidColorBrush(Color.FromArgb((byte)rgba,
        (byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8)));

    private enum Tool { Paint, Erase, Fill, Eyedropper, Line, Rect }
}
