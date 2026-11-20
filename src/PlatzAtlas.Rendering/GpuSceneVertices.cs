using System.Runtime.InteropServices;

namespace PlatzAtlas.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct GpuSceneVertex(float x, float y, RenderColor color, float u = 0, float v = 0,
    int offset = 0, int imageWidth = 0, int imageHeight = 0)
{
    public readonly float X = x, Y = y;
    public readonly float R = color.R, G = color.G, B = color.B;
    public readonly float U = u, V = v;
    public readonly float Offset = offset, ImageWidth = imageWidth, ImageHeight = imageHeight;
}

internal sealed class GpuImageStrip
{
    private RasterTexture[] _previous = [];
    private Dictionary<RasterTexture, int> _offsets = new(ReferenceEqualityComparer.Instance);
    public byte[] Bytes { get; private set; } = new byte[4];

    public bool Update(RenderSnapshot snapshot)
    {
        var images = snapshot.Sprites.Select(sprite => sprite.Image).Distinct<RasterTexture>(ReferenceEqualityComparer.Instance).ToArray();
        if (images.SequenceEqual(_previous, ReferenceEqualityComparer.Instance))
            return false;
        var texels = 1 + images.Sum(image => checked(image.Width * image.Height));
        var bytes = new byte[checked(texels * 4)];
        var offsets = new Dictionary<RasterTexture, int>(ReferenceEqualityComparer.Instance);
        var offset = 1;
        foreach (var image in images)
        {
            offsets.Add(image, offset);
            image.Rgba.Span.CopyTo(bytes.AsSpan(offset * 4));
            offset += image.Width * image.Height;
        }
        _previous = images;
        _offsets = offsets;
        Bytes = bytes;
        return true;
    }

    public static int VertexCount(RenderSnapshot snapshot) =>
        checked((snapshot.Triangles.Count + snapshot.Foreground.Count) * 3 + snapshot.Sprites.Count * 6);

    public void Fill(RenderSnapshot snapshot, Span<GpuSceneVertex> vertices, Func<RenderColor, RenderColor>? convert,
        bool linearTexture)
    {
        int index = FillTriangles(snapshot.Triangles, vertices, convert);
        foreach (var item in snapshot.Sprites)
        {
            var image = item.Image;
            var offset = _offsets[image];
            var textureColor = new RenderColor(linearTexture ? 1 : 0, 0, 0);
            var a = new GpuSceneVertex(item.A.X, item.A.Y, textureColor, 0, 0, offset, image.Width, image.Height);
            var b = new GpuSceneVertex(item.B.X, item.B.Y, textureColor, 1, 0, offset, image.Width, image.Height);
            var c = new GpuSceneVertex(item.C.X, item.C.Y, textureColor, 1, 1, offset, image.Width, image.Height);
            var d = new GpuSceneVertex(item.D.X, item.D.Y, textureColor, 0, 1, offset, image.Width, image.Height);
            vertices[index++] = a; vertices[index++] = b; vertices[index++] = c;
            vertices[index++] = a; vertices[index++] = c; vertices[index++] = d;
        }
        FillTriangles(snapshot.Foreground, vertices[index..], convert);
    }

    private static int FillTriangles(IReadOnlyList<RenderTriangle> triangles, Span<GpuSceneVertex> vertices,
        Func<RenderColor, RenderColor>? convert)
    {
        var index = 0;
        foreach (var triangle in triangles)
        {
            vertices[index++] = Vertex(triangle.A, convert);
            vertices[index++] = Vertex(triangle.B, convert);
            vertices[index++] = Vertex(triangle.C, convert);
        }
        return index;
    }

    private static GpuSceneVertex Vertex(RenderVertex vertex, Func<RenderColor, RenderColor>? convert) =>
        new(vertex.X, vertex.Y, convert?.Invoke(vertex.Color) ?? vertex.Color);
}
