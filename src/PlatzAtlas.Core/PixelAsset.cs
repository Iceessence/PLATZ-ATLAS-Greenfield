using System.Buffers.Binary;

namespace PlatzAtlas.Core;

public sealed record PixelAsset(int Width, int Height, ReadOnlyMemory<byte> Rgba)
{
    public const int MaxDimension = 256;
    public const int MaxProjectPixels = 262144;
    public const string MediaType = "image/vnd.platz.rgba8";

    public static ProjectAsset Create(Guid id, string name, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension || rgba.Length != width * height * 4)
            throw new InvalidDataException($"A PNG raster must be 1–{MaxDimension} pixels in each dimension.");
        var bytes = new byte[4 + rgba.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)height);
        rgba.CopyTo(bytes.AsSpan(4));
        return new ProjectAsset(id, name, MediaType, Convert.ToBase64String(bytes), ProjectFormat.Digest(bytes));
    }

    public static PixelAsset Read(ProjectAsset asset)
    {
        if (asset.MediaType == PixelClip.MediaType)
            return PixelClip.Read(asset).Frames[0].Image;
        if (asset.MediaType != MediaType)
            throw new InvalidDataException("The image asset has an unsupported media type.");
        var bytes = VerifiedBytes(asset, MaxDimension * MaxDimension * 4 + 4);
        if (bytes.Length < 8)
            throw new InvalidDataException($"Image asset “{asset.Name}” has an invalid size.");
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension || bytes.Length != 4 + width * height * 4)
            throw new InvalidDataException($"Image asset “{asset.Name}” has invalid dimensions.");
        return new PixelAsset(width, height, bytes.AsMemory(4));
    }

    internal static byte[] VerifiedBytes(ProjectAsset asset, int maximum)
    {
        if (asset.ContentBase64.Length > ((maximum + 2) / 3) * 4 ||
            asset.Id == Guid.Empty || string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 160 ||
            asset.Name.Any(char.IsControl))
            throw new InvalidDataException("The image asset has an invalid name, type or size.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(asset.ContentBase64); }
        catch (FormatException error) { throw new InvalidDataException($"Image asset “{asset.Name}” has invalid bytes.", error); }
        if (bytes.Length > maximum || !string.Equals(ProjectFormat.Digest(bytes), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Image asset “{asset.Name}” has an invalid checksum or size.");
        return bytes;
    }

    internal static void ValidateCatalog(IReadOnlyList<ProjectAsset> assets, Guid projectId, Guid sceneId)
    {
        var ids = new HashSet<Guid> { projectId, sceneId };
        var pixels = 0;
        foreach (var asset in assets)
        {
            if (asset is null || !ids.Add(asset.Id))
                throw new InvalidDataException("Every asset needs a unique ID.");
            var clip = PixelClip.Read(asset);
            pixels += clip.Width * clip.Height * clip.Frames.Count;
            if (pixels > MaxProjectPixels)
                throw new InvalidDataException("The project has reached its imported image budget.");
        }
    }

    public static void ValidateRuntime(IReadOnlyList<ProjectAsset> assets, IReadOnlyList<SceneEntity> entities,
        Guid projectId, Guid sceneId)
    {
        ValidateCatalog(assets, projectId, sceneId);
        var used = entities.Where(item => item.Component is SceneSprite).Select(item => ((SceneSprite)item.Component).AssetId).ToHashSet();
        if (!used.SetEquals(assets.Select(asset => asset.Id)) || entities.Any(item => used.Contains(item.Id)))
            throw new InvalidDataException("The room package has a missing or unexpected image asset.");
    }
}
