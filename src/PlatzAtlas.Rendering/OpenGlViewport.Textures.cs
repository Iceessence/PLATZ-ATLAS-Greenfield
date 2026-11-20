using System.Runtime.InteropServices;

namespace PlatzAtlas.Rendering;

public sealed unsafe partial class OpenGlViewport
{
    private const uint TextureBuffer = 0x8C2A;
    private const uint MaxTextureBufferSize = 0x8C2B;
    private const uint Rgba8 = 0x8058;
    private readonly GpuImageStrip _imageStrip = new();
    private uint _imageBuffer;
    private uint _imageTexture;
    private int _maxImageTexels;

    private void CreateTextureBuffer()
    {
        int maxTexels = 0;
        glGetIntegerv(MaxTextureBufferSize, &maxTexels);
        _maxImageTexels = maxTexels;
        uint texture = 0, buffer = 0;
        glGenTextures(1, &texture);
        _imageTexture = texture;
        _glGenBuffers(1, &buffer);
        _imageBuffer = buffer;
        UploadImageStrip();
        var texBuffer = (delegate* unmanaged[Stdcall]<uint, uint, uint, void>)RequireProc("glTexBuffer");
        glBindTexture(TextureBuffer, _imageTexture);
        texBuffer(TextureBuffer, Rgba8, _imageBuffer);
        glEnable(0x0BE2); // GL_BLEND
        const uint sourceAlpha = 0x0302, inverseSourceAlpha = 0x0303, one = 1;
        var blendSeparate = (delegate* unmanaged[Stdcall]<uint, uint, uint, uint, void>)RequireProc("glBlendFuncSeparate");
        blendSeparate(sourceAlpha, inverseSourceAlpha, one, inverseSourceAlpha);
    }

    private void UploadImageStrip()
    {
        if (_imageStrip.Bytes.Length / 4 > _maxImageTexels)
            throw new OpenGlViewportException("The imported images exceed this GPU's texture buffer size.");
        _glBindBuffer(TextureBuffer, _imageBuffer);
        fixed (byte* image = _imageStrip.Bytes)
            _glBufferData(TextureBuffer, _imageStrip.Bytes.Length, image, GlStreamDraw);
        _glBindBuffer(TextureBuffer, 0);
    }

    private void DestroyTextureBuffer()
    {
        if (_imageTexture != 0)
        {
            uint texture = _imageTexture;
            glDeleteTextures(1, &texture);
        }
        if (_imageBuffer != 0)
        {
            uint buffer = _imageBuffer;
            _glDeleteBuffers(1, &buffer);
        }
    }

    [DllImport("opengl32.dll")]
    private static extern void glGetIntegerv(uint name, int* value);
    [DllImport("opengl32.dll")]
    private static extern void glGenTextures(int count, uint* textures);
    [DllImport("opengl32.dll")]
    private static extern void glDeleteTextures(int count, uint* textures);
    [DllImport("opengl32.dll")]
    private static extern void glBindTexture(uint target, uint texture);
    [DllImport("opengl32.dll")]
    private static extern void glEnable(uint mode);
}
