namespace PlatzAtlas.Rendering;

/// <summary>Requested presentation backend for a viewport.</summary>
public enum RendererPreference
{
    Auto,
    Vulkan,
    OpenGl,
    CpuReference
}

/// <summary>An sRGB display color. Components range from zero to one.</summary>
public readonly record struct RenderColor(float R, float G, float B)
{
    public static RenderColor FromRgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f);
}

/// <summary>Normalized coordinates relative to the top-left of the viewport.</summary>
public readonly record struct RenderVertex(float X, float Y, RenderColor Color);

public readonly record struct RenderTriangle(RenderVertex A, RenderVertex B, RenderVertex C);
public readonly record struct RenderPoint(float X, float Y);

public sealed record RasterTexture(int Width, int Height, ReadOnlyMemory<byte> Rgba);
public sealed record RenderSprite(RasterTexture Image, RenderPoint A, RenderPoint B, RenderPoint C, RenderPoint D);

/// <summary>Immutable draw data derived from one project revision. Draw triangles in list order.</summary>
public sealed class RenderSnapshot(
    Guid projectId, long revision, RenderColor background, IEnumerable<RenderTriangle> triangles,
    Guid? previewObjectId = null, Guid? playSessionId = null, long? playStep = null,
    IEnumerable<RenderSprite>? sprites = null, IEnumerable<RenderTriangle>? foreground = null)
{
    public Guid ProjectId { get; } = projectId;
    public long Revision { get; } = revision;
    public Guid? PreviewObjectId { get; } = previewObjectId;
    public Guid? PlaySessionId { get; } = playSessionId;
    public long? PlayStep { get; } = playStep;
    public RenderColor Background { get; } = background;
    public IReadOnlyList<RenderTriangle> Triangles { get; } = Array.AsReadOnly(triangles.ToArray());
    public IReadOnlyList<RenderSprite> Sprites { get; } = Array.AsReadOnly(sprites?.ToArray() ?? []);
    public IReadOnlyList<RenderTriangle> Foreground { get; } = Array.AsReadOnly(foreground?.ToArray() ?? []);
}
