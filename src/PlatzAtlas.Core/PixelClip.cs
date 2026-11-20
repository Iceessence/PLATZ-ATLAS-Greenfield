using System.Buffers.Binary;

namespace PlatzAtlas.Core;

public sealed record PixelFrame(int DurationMs, PixelAsset Image);

public sealed record PixelClip(int Width, int Height, IReadOnlyList<PixelFrame> Frames)
{
    public const string MediaType = "image/vnd.platz.rgba8-animation";
    public const int MaxFrames = 16;
    public const int DefaultDuration = 180;

    public int FrameAt(double milliseconds)
    {
        if (Frames.Count == 1 || !double.IsFinite(milliseconds) || milliseconds <= 0)
            return 0;
        var time = milliseconds % Frames.Sum(frame => frame.DurationMs);
        for (var index = 0; index < Frames.Count - 1; index++)
        {
            time -= Frames[index].DurationMs;
            if (time < 0)
                return index;
        }
        return Frames.Count - 1;
    }

    public static ProjectAsset Create(Guid id, string name, IReadOnlyList<PixelFrame> frames)
    {
        if (frames.Count is < 2 or > MaxFrames)
            throw new InvalidDataException("An animation needs 2–16 frames.");
        var width = frames[0].Image.Width;
        var height = frames[0].Image.Height;
        ValidateSize(width, height, frames.Count);
        var pixels = width * height * 4;
        var stride = pixels + 2;
        var bytes = new byte[6 + stride * frames.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)frames.Count);
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (frame.Image.Width != width || frame.Image.Height != height || frame.Image.Rgba.Length != pixels ||
                frame.DurationMs is < 40 or > 5000)
                throw new InvalidDataException("All frames must share dimensions and last 40–5000 ms.");
            var data = bytes.AsSpan(6 + index * stride, stride);
            BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)frame.DurationMs);
            frame.Image.Rgba.Span.CopyTo(data[2..]);
        }
        return new ProjectAsset(id, name, MediaType, Convert.ToBase64String(bytes), ProjectFormat.Digest(bytes));
    }

    public static PixelClip Read(ProjectAsset asset)
    {
        if (asset.MediaType != MediaType)
        {
            var image = PixelAsset.Read(asset);
            return new PixelClip(image.Width, image.Height, Array.AsReadOnly(new[] { new PixelFrame(DefaultDuration, image) }));
        }
        var bytes = PixelAsset.VerifiedBytes(asset, 6 + MaxFrames * 2 + PixelAsset.MaxProjectPixels * 4);
        if (bytes.Length < 6)
            throw new InvalidDataException("The animation header is incomplete.");
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        ValidateSize(width, height, count);
        var stride = width * height * 4 + 2;
        if (bytes.Length != 6 + stride * count)
            throw new InvalidDataException("The animation frame data is incomplete.");
        var frames = new PixelFrame[count];
        for (var index = 0; index < frames.Length; index++)
        {
            var offset = 6 + index * stride;
            var duration = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
            if (duration is < 40 or > 5000)
                throw new InvalidDataException("An animation frame has an invalid duration.");
            frames[index] = new PixelFrame(duration, new PixelAsset(width, height, bytes.AsMemory(offset + 2, stride - 2)));
        }
        return new PixelClip(width, height, Array.AsReadOnly(frames));
    }

    private static void ValidateSize(int width, int height, int count)
    {
        if (width is < 1 or > PixelAsset.MaxDimension || height is < 1 or > PixelAsset.MaxDimension ||
            count is < 2 or > MaxFrames || width * height * count > PixelAsset.MaxProjectPixels)
            throw new InvalidDataException("Animation dimensions or frame count exceed the project pixel budget.");
    }
}
