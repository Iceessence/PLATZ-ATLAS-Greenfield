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
        0x192331ff, 0x334b64ff, 0x6585a1ff, 0xc1d6dcff, 0xf7f4eaff,
        0x654260ff, 0xab5788ff, 0xdd847fff, 0xf0ba83ff, 0xffdc8fff,
        0x245349ff, 0x338678ff, 0x51b785ff, 0xaed58fff, 0x78b5eaff
    ];
    private readonly PixelDraft _draft;
    private Point? _last;
    private int _x, _y;
    private Tool _tool = Tool.Paint;
    private uint _strokeColor;

    internal PixelEditorWindow(ProjectAsset? source)
    {
        var image = source is null ? null : PixelAsset.Read(source);
        _draft = new PixelDraft(image is null || Math.Max(image.Width, image.Height) > 16 ? 32 : 16, image);
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
        _draft.EndStroke();
        if (PixelSurface.IsMouseCaptured)
            PixelSurface.ReleaseMouseCapture();
    }

    private void SizeClicked(object sender, RoutedEventArgs e)
    {
        FinishStroke();
        if (sender is Button { Tag: string value } && int.TryParse(value, out var size))
            _draft.Resize(size);
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
        FinishStroke();
        _draft.Undo();
        RefreshPixels();
    }

    private void RedoClicked(object sender, RoutedEventArgs e)
    {
        FinishStroke();
        _draft.Redo();
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
        FinishStroke();
        var name = AssetName.Text.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            PixelStatus.Text = "Enter a printable asset name.";
            AssetName.Focus();
            return;
        }
        Result = PixelAsset.Create(Guid.NewGuid(), name, _draft.Size, _draft.Size, _draft.CopyRgba());
        DialogResult = true;
    }

    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        FinishStroke();
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

    private void SelectColor(uint color) => PixelColor.Text = $"{color:X8}";

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
        PixelImage.Source = CreateBitmap();
        PixelUndo.IsEnabled = _draft.CanUndo;
        PixelRedo.IsEnabled = _draft.CanRedo;
        Pixels16.IsEnabled = _draft.Size != 16;
        Pixels32.IsEnabled = _draft.Size != 32;
        foreach (var button in ToolButtons.Children.OfType<Button>())
            button.BorderBrush = (Brush)FindResource(button.Tag.ToString() == _tool.ToString() ? "Accent" : "Border");
        PixelStatus.Text = $"{_tool} · Pixel {_x + 1}, {_y + 1}";
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

    private enum Tool { Paint, Erase, Fill, Eyedropper }
}
