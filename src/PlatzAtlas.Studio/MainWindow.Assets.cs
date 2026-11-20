using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PlatzAtlas.Core;
using PlatzAtlas.RoomView;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    private void ImportPngClicked(object sender, RoutedEventArgs e)
    {
        if (_play is not null || _session.Current.Scene is null)
            return;
        var dialog = new OpenFileDialog { Title = "Import a PNG image", Filter = "PNG image (*.png)|*.png", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
            ImportPngFile(dialog.FileName);
    }

    private void ImportPngFile(string path)
    {
        try
        {
            var asset = DecodePng(path);
            ApplyScene(new ImportSceneAsset(_session.Current.ProjectId, _session.Current.Revision, asset),
                $"Imported {asset.Name}. Choose Place selected image to add it to the room.");
            SceneAssetList.SelectedItem = SceneAssetList.Items.OfType<AssetItem>().FirstOrDefault(item => item.Id == asset.Id)
                ?? SceneAssetList.SelectedItem;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            FormatException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            ShowStatus($"Could not import PNG. {error.Message}", error: true);
        }
    }

    private static ProjectAsset DecodePng(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length is < 24 or > 2 * 1024 * 1024)
            throw new InvalidDataException("Choose a PNG file up to 2 MiB.");
        var encoded = new byte[(int)file.Length];
        file.ReadExactly(encoded);
        var header = encoded.AsSpan(0, 24);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("The file does not have a PNG header.");
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..]);
        if (width is < 1 or > PixelAsset.MaxDimension || height is < 1 or > PixelAsset.MaxDimension)
            throw new InvalidDataException($"Choose a PNG up to {PixelAsset.MaxDimension} × {PixelAsset.MaxDimension} pixels.");
        ValidatePngChunks(encoded);
        using var source = new MemoryStream(encoded, writable: false);
        var frame = new PngBitmapDecoder(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth != width || frame.PixelHeight != height)
            throw new InvalidDataException("The PNG dimensions do not match its header.");
        var raster = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[width * height * 4];
        raster.CopyPixels(bytes, width * 4, 0);
        for (var index = 0; index < bytes.Length; index += 4)
            (bytes[index], bytes[index + 2]) = (bytes[index + 2], bytes[index]);
        return PixelAsset.Create(Guid.NewGuid(), Path.GetFileNameWithoutExtension(path), width, height, bytes);
    }

    private static void ValidatePngChunks(ReadOnlySpan<byte> file)
    {
        var hasImage = false;
        var offset = 8;
        while (offset <= file.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(file[offset..]);
            if (length > file.Length - offset - 12)
                break;
            int end = offset + 8 + (int)length;
            uint crc = uint.MaxValue;
            foreach (var value in file[(offset + 4)..end])
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(file[end..]))
                throw new InvalidDataException("A PNG image chunk has an invalid checksum.");
            var type = file.Slice(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8))
                hasImage = true;
            if (type.SequenceEqual("IEND"u8))
            {
                if (hasImage && length == 0 && end + 4 == file.Length)
                    return;
                break;
            }
            offset = end + 4;
        }
        throw new InvalidDataException("The PNG image is incomplete.");
    }

    private void RefreshAssetBrowser()
    {
        var active = _session.Current.Scene?.ActiveAssetIds;
        Guid? prior = (SceneAssetList.SelectedItem as AssetItem)?.Id;
        var items = _session.Current.Assets.Where(item => active?.Contains(item.Id) == true)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item =>
            {
                var dimensions = SceneArt.Image(item);
                return new AssetItem(item.Id, item.Name, dimensions.Width, dimensions.Height);
            }).ToArray();
        SceneAssetList.ItemsSource = items;
        SceneAssetList.SelectedItem = items.FirstOrDefault(item => item.Id == prior) ?? items.FirstOrDefault();
        PlaceAsset.IsEnabled = SceneAssetList.SelectedItem is not null;
    }

    private void SceneAssetSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PlaceAsset is not null)
            PlaceAsset.IsEnabled = SceneAssetList.SelectedItem is not null;
    }

    private void PlaceAssetClicked(object sender, RoutedEventArgs e)
    {
        if (_play is not null || SceneAssetList.SelectedItem is not AssetItem asset || _session.Current.Scene is not { } scene)
            return;
        var offset = .045 * (scene.Entities.Count % 5);
        var width = Math.Clamp(.29 * asset.Width / Math.Max(asset.Width, asset.Height), .025, .29);
        var height = Math.Clamp(width * 12 / 7 * asset.Height / asset.Width, .025, .46);
        var sprite = new SceneEntity(Guid.NewGuid(), asset.Name.Length <= 96 ? asset.Name : asset.Name[..96],
            new Transform2D(.44 + offset, .43 + offset, width, height, 0), new SceneSprite(asset.Id));
        ApplyScene(new AddSceneEntity(_session.Current.ProjectId, _session.Current.Revision, sprite),
            $"Placed {sprite.Name}. Drag it or edit its transform.");
        SideTabs.SelectedItem = InspectorTab;
    }

    private sealed record AssetItem(Guid Id, string Name, int Width, int Height)
    {
        public string Label => $"{Name} · {Width} × {Height}";
    }
}
