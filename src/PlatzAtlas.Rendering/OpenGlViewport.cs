using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PlatzAtlas.Rendering;

public sealed class OpenGlViewportException(string message, Exception? cause = null) : Exception(message, cause);

/// <summary>A single-threaded, on-demand OpenGL compatibility renderer for a caller-owned HWND.</summary>
public sealed unsafe partial class OpenGlViewport : IDisposable
{
    private const uint ColorBufferBit = 0x00004000;
    private const uint GlTriangles = 0x0004;
    private const uint GlFloat = 0x1406;
    private const uint GlUnsignedByte = 0x1401;
    private const uint GlArrayBuffer = 0x8892;
    private const uint GlStreamDraw = 0x88E0;
    private const uint GlFragmentShader = 0x8B30;
    private const uint GlVertexShader = 0x8B31;
    private const uint GlCompileStatus = 0x8B81;
    private const uint GlLinkStatus = 0x8B82;
    private const uint GlInfoLogLength = 0x8B84;
    private const uint GlVendor = 0x1F00;
    private const uint GlRenderer = 0x1F01;
    private const uint GlVersion = 0x1F02;
    private const uint GlBgra = 0x80E1;
    private const uint GlPackAlignment = 0x0D05;
    private const uint GlTimeElapsed = 0x88BF;
    private const uint GlQueryResult = 0x8866;
    private const uint GlQueryResultAvailable = 0x8867;

    private const uint PfdDoubleBuffer = 0x00000001;
    private const uint PfdDrawToWindow = 0x00000004;
    private const uint PfdSupportOpengl = 0x00000020;
    private const int WglContextMajorVersionArb = 0x2091;
    private const int WglContextMinorVersionArb = 0x2092;
    private const int WglContextProfileMaskArb = 0x9126;
    private const int WglContextCoreProfileBitArb = 0x00000001;

    private readonly nint _window;
    private nint _hdc;
    private nint _context;
    private uint _program;
    private uint _vao;
    private uint _vbo;
    private uint _timeQuery;
    private bool _queryPending;
    private bool _simulateDeviceLoss;
    private bool _disposed;
    private long _frames;
    private GpuSceneVertex[] _uploadScratch = [];
    private byte[] _readbackScratch = [];

    private delegate* unmanaged[Stdcall]<uint, uint> _glCreateShader;
    private delegate* unmanaged[Stdcall]<uint, int, byte**, int*, void> _glShaderSource;
    private delegate* unmanaged[Stdcall]<uint, void> _glCompileShader;
    private delegate* unmanaged[Stdcall]<uint, uint, int*, void> _glGetShaderiv;
    private delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void> _glGetShaderInfoLog;
    private delegate* unmanaged[Stdcall]<uint> _glCreateProgram;
    private delegate* unmanaged[Stdcall]<uint, uint, void> _glAttachShader;
    private delegate* unmanaged[Stdcall]<uint, void> _glLinkProgram;
    private delegate* unmanaged[Stdcall]<uint, uint, int*, void> _glGetProgramiv;
    private delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void> _glGetProgramInfoLog;
    private delegate* unmanaged[Stdcall]<uint, void> _glUseProgram;
    private delegate* unmanaged[Stdcall]<uint, void> _glDeleteShader;
    private delegate* unmanaged[Stdcall]<uint, void> _glDeleteProgram;
    private delegate* unmanaged[Stdcall]<int, uint*, void> _glGenVertexArrays;
    private delegate* unmanaged[Stdcall]<uint, void> _glBindVertexArray;
    private delegate* unmanaged[Stdcall]<int, uint*, void> _glDeleteVertexArrays;
    private delegate* unmanaged[Stdcall]<int, uint*, void> _glGenBuffers;
    private delegate* unmanaged[Stdcall]<uint, uint, void> _glBindBuffer;
    private delegate* unmanaged[Stdcall]<uint, nint, void*, uint, void> _glBufferData;
    private delegate* unmanaged[Stdcall]<int, uint*, void> _glDeleteBuffers;
    private delegate* unmanaged[Stdcall]<uint, void> _glEnableVertexAttribArray;
    private delegate* unmanaged[Stdcall]<uint, int, uint, byte, int, nint, void> _glVertexAttribPointer;
    private delegate* unmanaged[Stdcall]<int, uint*, void> _glGenQueries;
    private delegate* unmanaged[Stdcall]<int, uint*, void> _glDeleteQueries;
    private delegate* unmanaged[Stdcall]<uint, uint, void> _glBeginQuery;
    private delegate* unmanaged[Stdcall]<uint, void> _glEndQuery;
    private delegate* unmanaged[Stdcall]<uint, uint, int*, void> _glGetQueryObjectiv;
    private delegate* unmanaged[Stdcall]<uint, uint, ulong*, void> _glGetQueryObjectui64v;

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort Size;
        public ushort Version;
        public uint Flags;
        public byte PixelType;
        public byte ColorBits;
        public byte RedBits;
        public byte RedShift;
        public byte GreenBits;
        public byte GreenShift;
        public byte BlueBits;
        public byte BlueShift;
        public byte AlphaBits;
        public byte AlphaShift;
        public byte AccumBits;
        public byte AccumRedBits;
        public byte AccumGreenBits;
        public byte AccumBlueBits;
        public byte AccumAlphaBits;
        public byte DepthBits;
        public byte StencilBits;
        public byte AuxBuffers;
        public byte LayerType;
        public byte Reserved;
        public uint LayerMask;
        public uint VisibleMask;
        public uint DamageMask;
    }

    public string DeviceName { get; private set; } = "";
    public string Diagnostics { get; private set; } = "";

    public OpenGlViewport(nint windowHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        _window = windowHandle;
        try
        {
            CreateContext();
            LoadEntryPoints();
            CreatePipelineResources();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Dispose();
            throw new OpenGlViewportException("The system OpenGL driver could not start.", ex);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SimulateDeviceLoss()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _simulateDeviceLoss = true;
    }

    public VulkanFrame Present(RenderSnapshot snapshot, int pixelWidth, int pixelHeight, Span<byte> bgraReadback = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        if (_simulateDeviceLoss)
            throw new OpenGlViewportException("OpenGL context lost (simulated).");

        int requiredBytes = checked(pixelWidth * pixelHeight * 4);
        if (!bgraReadback.IsEmpty && bgraReadback.Length < requiredBytes)
            throw new ArgumentException($"Readback buffer requires at least {requiredBytes} bytes.", nameof(bgraReadback));

        long started = Stopwatch.GetTimestamp();
        if (!wglMakeCurrent(_hdc, _context))
            throw new OpenGlViewportException("OpenGL could not bind its rendering context to the viewport window.");

        double? previousGpuMs = TryReadPendingGpuTime();
        bool timingThisFrame = _timeQuery != 0 && !_queryPending;
        if (timingThisFrame)
            _glBeginQuery(GlTimeElapsed, _timeQuery);

        glViewport(0, 0, pixelWidth, pixelHeight);
        glClearColor(snapshot.Background.R, snapshot.Background.G, snapshot.Background.B, 1f);
        glClear(ColorBufferBit);

        int vertexCount = UploadVertices(snapshot);
        if (vertexCount > 0)
        {
            _glUseProgram(_program);
            _glBindVertexArray(_vao);
            glDrawArrays(GlTriangles, 0, vertexCount);
            _glBindVertexArray(0);
        }

        if (timingThisFrame)
        {
            _glEndQuery(GlTimeElapsed);
            _queryPending = true;
        }

        if (!bgraReadback.IsEmpty)
            ReadBackFramebuffer(pixelWidth, pixelHeight, requiredBytes, bgraReadback);

        if (!SwapBuffers(_hdc))
            throw new OpenGlViewportException($"OpenGL could not present the frame (Win32 {Marshal.GetLastWin32Error()}).");

        uint glError = glGetError();
        if (glError != 0)
            throw new OpenGlViewportException($"OpenGL reported an error during presentation (0x{glError:x4}).");

        double? currentGpuMs = TryReadPendingGpuTime() ?? previousGpuMs;
        return new VulkanFrame(pixelWidth, pixelHeight, ++_frames, Stopwatch.GetElapsedTime(started).TotalMilliseconds)
        {
            GpuMilliseconds = currentGpuMs
        };
    }

    private void CreateContext()
    {
        _hdc = GetDC(_window);
        if (_hdc == 0)
            throw new OpenGlViewportException("OpenGL could not obtain a device context for the viewport window.");

        var descriptor = new PixelFormatDescriptor
        {
            Size = (ushort)sizeof(PixelFormatDescriptor),
            Version = 1,
            Flags = PfdDrawToWindow | PfdSupportOpengl | PfdDoubleBuffer,
            PixelType = 0,
            ColorBits = 32,
            AlphaBits = 8,
            DepthBits = 24,
            LayerType = 0
        };

        int format = ChoosePixelFormat(_hdc, &descriptor);
        if (format == 0 || !SetPixelFormat(_hdc, format, &descriptor))
            throw new OpenGlViewportException($"OpenGL could not select a 32-bit pixel format (Win32 {Marshal.GetLastWin32Error()}).");

        nint bootstrap = wglCreateContext(_hdc);
        if (bootstrap == 0 || !wglMakeCurrent(_hdc, bootstrap))
        {
            if (bootstrap != 0)
                wglDeleteContext(bootstrap);
            throw new OpenGlViewportException($"OpenGL could not create a rendering context (Win32 {Marshal.GetLastWin32Error()}).");
        }

        _context = bootstrap;
        string profile = "Compatibility";
        nint createAttribsPtr = wglGetProcAddress("wglCreateContextAttribsARB");
        if (createAttribsPtr != 0)
        {
            var createAttribs = (delegate* unmanaged[Stdcall]<nint, nint, int*, nint>)createAttribsPtr;
            int* attribs = stackalloc int[7]
            {
                WglContextMajorVersionArb, 3,
                WglContextMinorVersionArb, 3,
                WglContextProfileMaskArb, WglContextCoreProfileBitArb,
                0
            };
            nint core = createAttribs(_hdc, 0, attribs);
            if (core != 0 && wglMakeCurrent(_hdc, core))
            {
                _context = core;
                wglDeleteContext(bootstrap);
                profile = "3.3 Core";
            }
            else if (core != 0)
            {
                wglDeleteContext(core);
                wglMakeCurrent(_hdc, bootstrap);
            }
        }

        string swapMode = "default swap";
        nint swapIntervalPtr = wglGetProcAddress("wglSwapIntervalEXT");
        if (swapIntervalPtr != 0)
        {
            var swapInterval = (delegate* unmanaged[Stdcall]<int, int>)swapIntervalPtr;
            if (swapInterval(1) != 0)
                swapMode = "VSync";
        }

        string renderer = Marshal.PtrToStringUTF8((nint)glGetString(GlRenderer)) ?? "OpenGL adapter";
        string vendor = Marshal.PtrToStringUTF8((nint)glGetString(GlVendor)) ?? "";
        string version = Marshal.PtrToStringUTF8((nint)glGetString(GlVersion)) ?? "";
        DeviceName = string.IsNullOrWhiteSpace(renderer) ? vendor : renderer;
        Diagnostics = $"{DeviceName}; OpenGL {version}; profile {profile}; {swapMode}; RGBA8";
    }

    private void LoadEntryPoints()
    {
        _glCreateShader = (delegate* unmanaged[Stdcall]<uint, uint>)RequireProc("glCreateShader");
        _glShaderSource = (delegate* unmanaged[Stdcall]<uint, int, byte**, int*, void>)RequireProc("glShaderSource");
        _glCompileShader = (delegate* unmanaged[Stdcall]<uint, void>)RequireProc("glCompileShader");
        _glGetShaderiv = (delegate* unmanaged[Stdcall]<uint, uint, int*, void>)RequireProc("glGetShaderiv");
        _glGetShaderInfoLog = (delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void>)RequireProc("glGetShaderInfoLog");
        _glCreateProgram = (delegate* unmanaged[Stdcall]<uint>)RequireProc("glCreateProgram");
        _glAttachShader = (delegate* unmanaged[Stdcall]<uint, uint, void>)RequireProc("glAttachShader");
        _glLinkProgram = (delegate* unmanaged[Stdcall]<uint, void>)RequireProc("glLinkProgram");
        _glGetProgramiv = (delegate* unmanaged[Stdcall]<uint, uint, int*, void>)RequireProc("glGetProgramiv");
        _glGetProgramInfoLog = (delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void>)RequireProc("glGetProgramInfoLog");
        _glUseProgram = (delegate* unmanaged[Stdcall]<uint, void>)RequireProc("glUseProgram");
        _glDeleteShader = (delegate* unmanaged[Stdcall]<uint, void>)RequireProc("glDeleteShader");
        _glDeleteProgram = (delegate* unmanaged[Stdcall]<uint, void>)RequireProc("glDeleteProgram");
        _glGenVertexArrays = (delegate* unmanaged[Stdcall]<int, uint*, void>)RequireProc("glGenVertexArrays");
        _glBindVertexArray = (delegate* unmanaged[Stdcall]<uint, void>)RequireProc("glBindVertexArray");
        _glDeleteVertexArrays = (delegate* unmanaged[Stdcall]<int, uint*, void>)RequireProc("glDeleteVertexArrays");
        _glGenBuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)RequireProc("glGenBuffers");
        _glBindBuffer = (delegate* unmanaged[Stdcall]<uint, uint, void>)RequireProc("glBindBuffer");
        _glBufferData = (delegate* unmanaged[Stdcall]<uint, nint, void*, uint, void>)RequireProc("glBufferData");
        _glDeleteBuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)RequireProc("glDeleteBuffers");
        _glEnableVertexAttribArray = (delegate* unmanaged[Stdcall]<uint, void>)RequireProc("glEnableVertexAttribArray");
        _glVertexAttribPointer = (delegate* unmanaged[Stdcall]<uint, int, uint, byte, int, nint, void>)RequireProc("glVertexAttribPointer");

        nint genQueries = wglGetProcAddress("glGenQueries");
        nint deleteQueries = wglGetProcAddress("glDeleteQueries");
        nint beginQuery = wglGetProcAddress("glBeginQuery");
        nint endQuery = wglGetProcAddress("glEndQuery");
        nint getQueryiv = wglGetProcAddress("glGetQueryObjectiv");
        nint getQueryui64v = wglGetProcAddress("glGetQueryObjectui64v");
        if (genQueries != 0 && deleteQueries != 0 && beginQuery != 0 && endQuery != 0 && getQueryiv != 0 && getQueryui64v != 0)
        {
            _glGenQueries = (delegate* unmanaged[Stdcall]<int, uint*, void>)genQueries;
            _glDeleteQueries = (delegate* unmanaged[Stdcall]<int, uint*, void>)deleteQueries;
            _glBeginQuery = (delegate* unmanaged[Stdcall]<uint, uint, void>)beginQuery;
            _glEndQuery = (delegate* unmanaged[Stdcall]<uint, void>)endQuery;
            _glGetQueryObjectiv = (delegate* unmanaged[Stdcall]<uint, uint, int*, void>)getQueryiv;
            _glGetQueryObjectui64v = (delegate* unmanaged[Stdcall]<uint, uint, ulong*, void>)getQueryui64v;
        }
    }

    private static nint RequireProc(string name)
    {
        nint proc = wglGetProcAddress(name);
        if (proc is 0 or 1 or 2 or 3 or -1)
            throw new OpenGlViewportException($"OpenGL entry point {name} is unavailable on this driver.");
        return proc;
    }

    private void CreatePipelineResources()
    {
        const string vertexSource = """
            #version 330 core
            layout(location = 0) in vec2 position;
            layout(location = 1) in vec3 color;
            layout(location = 2) in vec2 imageUv;
            layout(location = 3) in vec3 imageData;
            out vec3 vertexColor;
            out vec2 uv;
            flat out ivec3 image;
            void main()
            {
                gl_Position = vec4(position.x * 2.0 - 1.0, 1.0 - position.y * 2.0, 0.0, 1.0);
                vertexColor = color;
                uv = imageUv;
                image = ivec3(imageData);
            }
            """;

        const string fragmentSource = """
            #version 330 core
            in vec3 vertexColor;
            in vec2 uv;
            flat in ivec3 image;
            uniform samplerBuffer imageStrip;
            out vec4 fragmentColor;
            void main()
            {
                if (image.y == 0)
                {
                    fragmentColor = vec4(vertexColor, 1.0);
                    return;
                }
                ivec2 pixel = clamp(ivec2(uv * vec2(image.yz)), ivec2(0), image.yz - 1);
                fragmentColor = texelFetch(imageStrip, image.x + pixel.y * image.y + pixel.x);
            }
            """;

        uint vertex = 0;
        uint fragment = 0;
        try
        {
            vertex = CompileShader(GlVertexShader, vertexSource);
            fragment = CompileShader(GlFragmentShader, fragmentSource);
            _program = _glCreateProgram();
            _glAttachShader(_program, vertex);
            _glAttachShader(_program, fragment);
            _glLinkProgram(_program);

            int linked = 0;
            _glGetProgramiv(_program, GlLinkStatus, &linked);
            if (linked == 0)
                throw new OpenGlViewportException($"OpenGL could not link viewport shader: {ReadProgramLog(_program)}");
        }
        finally
        {
            if (fragment != 0)
                _glDeleteShader(fragment);
            if (vertex != 0)
                _glDeleteShader(vertex);
        }

        uint vao = 0;
        uint vbo = 0;
        _glGenVertexArrays(1, &vao);
        _glGenBuffers(1, &vbo);
        _vao = vao;
        _vbo = vbo;

        _glBindVertexArray(_vao);
        _glBindBuffer(GlArrayBuffer, _vbo);
        _glEnableVertexAttribArray(0);
        _glVertexAttribPointer(0, 2, GlFloat, 0, sizeof(GpuSceneVertex), 0);
        _glEnableVertexAttribArray(1);
        _glVertexAttribPointer(1, 3, GlFloat, 0, sizeof(GpuSceneVertex), 2 * sizeof(float));
        _glEnableVertexAttribArray(2);
        _glVertexAttribPointer(2, 2, GlFloat, 0, sizeof(GpuSceneVertex), 5 * sizeof(float));
        _glEnableVertexAttribArray(3);
        _glVertexAttribPointer(3, 3, GlFloat, 0, sizeof(GpuSceneVertex), 7 * sizeof(float));
        _glBindVertexArray(0);
        _glBindBuffer(GlArrayBuffer, 0);
        CreateTextureBuffer();

        if (_glGenQueries != null)
        {
            uint query = 0;
            _glGenQueries(1, &query);
            _timeQuery = query;
        }
    }

    private uint CompileShader(uint stage, string source)
    {
        uint shader = _glCreateShader(stage);
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        int length = bytes.Length;
        fixed (byte* ptr = bytes)
        {
            byte* p = ptr;
            _glShaderSource(shader, 1, &p, &length);
        }
        _glCompileShader(shader);
        int compiled = 0;
        _glGetShaderiv(shader, GlCompileStatus, &compiled);
        if (compiled == 0)
        {
            string log = ReadShaderLog(shader);
            _glDeleteShader(shader);
            throw new OpenGlViewportException($"OpenGL could not compile viewport shader: {log}");
        }
        return shader;
    }

    private string ReadShaderLog(uint shader)
    {
        int length = 0;
        _glGetShaderiv(shader, GlInfoLogLength, &length);
        if (length <= 1)
            return "unknown error";
        byte[] buffer = new byte[length];
        fixed (byte* ptr = buffer)
        {
            int actual = 0;
            _glGetShaderInfoLog(shader, length, &actual, ptr);
            return Encoding.UTF8.GetString(buffer, 0, actual).Trim();
        }
    }

    private string ReadProgramLog(uint program)
    {
        int length = 0;
        _glGetProgramiv(program, GlInfoLogLength, &length);
        if (length <= 1)
            return "unknown error";
        byte[] buffer = new byte[length];
        fixed (byte* ptr = buffer)
        {
            int actual = 0;
            _glGetProgramInfoLog(program, length, &actual, ptr);
            return Encoding.UTF8.GetString(buffer, 0, actual).Trim();
        }
    }

    private int UploadVertices(RenderSnapshot snapshot)
    {
        if (_imageStrip.Update(snapshot))
            UploadImageStrip();
        int vertexCount = GpuImageStrip.VertexCount(snapshot);
        if (vertexCount == 0)
            return 0;

        if (_uploadScratch.Length < vertexCount)
            _uploadScratch = new GpuSceneVertex[Math.Max(vertexCount, 64)];
        _imageStrip.Fill(snapshot, _uploadScratch, null, linearTexture: false);

        _glBindBuffer(GlArrayBuffer, _vbo);
        fixed (GpuSceneVertex* first = _uploadScratch)
            _glBufferData(GlArrayBuffer, checked(vertexCount * sizeof(GpuSceneVertex)), first, GlStreamDraw);
        _glBindBuffer(GlArrayBuffer, 0);
        return vertexCount;
    }

    private void ReadBackFramebuffer(int width, int height, int byteCount, Span<byte> destination)
    {
        if (_readbackScratch.Length < byteCount)
            _readbackScratch = new byte[byteCount];

        glPixelStorei(GlPackAlignment, 1);
        fixed (byte* ptr = _readbackScratch)
            glReadPixels(0, 0, width, height, GlBgra, GlUnsignedByte, ptr);

        int stride = width * 4;
        for (int row = 0; row < height; row++)
        {
            int srcOffset = (height - 1 - row) * stride;
            _readbackScratch.AsSpan(srcOffset, stride).CopyTo(destination.Slice(row * stride, stride));
        }
    }

    private double? TryReadPendingGpuTime()
    {
        if (!_queryPending || _timeQuery == 0)
            return null;

        int available = 0;
        _glGetQueryObjectiv(_timeQuery, GlQueryResultAvailable, &available);
        if (available == 0)
            return null;

        ulong nanoseconds = 0;
        _glGetQueryObjectui64v(_timeQuery, GlQueryResult, &nanoseconds);
        _queryPending = false;
        return nanoseconds / 1_000_000.0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_hdc != 0 && _context != 0)
        {
            if (wglMakeCurrent(_hdc, _context))
            {
                DestroyTextureBuffer();
                if (_timeQuery != 0 && _glDeleteQueries != null)
                {
                    uint q = _timeQuery;
                    _glDeleteQueries(1, &q);
                    _timeQuery = 0;
                }
                if (_vbo != 0 && _glDeleteBuffers != null)
                {
                    uint b = _vbo;
                    _glDeleteBuffers(1, &b);
                    _vbo = 0;
                }
                if (_vao != 0 && _glDeleteVertexArrays != null)
                {
                    uint a = _vao;
                    _glDeleteVertexArrays(1, &a);
                    _vao = 0;
                }
                if (_program != 0 && _glDeleteProgram != null)
                {
                    _glDeleteProgram(_program);
                    _program = 0;
                }
                wglMakeCurrent(0, 0);
            }
            wglDeleteContext(_context);
            _context = 0;
        }

        if (_hdc != 0)
        {
            ReleaseDC(_window, _hdc);
            _hdc = 0;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int ChoosePixelFormat(nint hdc, PixelFormatDescriptor* ppfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPixelFormat(nint hdc, int format, PixelFormatDescriptor* ppfd);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SwapBuffers(nint hdc);

    [DllImport("opengl32.dll", SetLastError = true)]
    private static extern nint wglCreateContext(nint hdc);

    [DllImport("opengl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool wglDeleteContext(nint hglrc);

    [DllImport("opengl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool wglMakeCurrent(nint hdc, nint hglrc);

    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
    private static extern nint wglGetProcAddress(string procName);

    [DllImport("opengl32.dll")]
    private static extern void glViewport(int x, int y, int width, int height);

    [DllImport("opengl32.dll")]
    private static extern void glClearColor(float red, float green, float blue, float alpha);

    [DllImport("opengl32.dll")]
    private static extern void glClear(uint mask);

    [DllImport("opengl32.dll")]
    private static extern void glDrawArrays(uint mode, int first, int count);

    [DllImport("opengl32.dll")]
    private static extern void glPixelStorei(uint pname, int param);

    [DllImport("opengl32.dll")]
    private static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, void* pixels);

    [DllImport("opengl32.dll")]
    private static extern uint glGetError();

    [DllImport("opengl32.dll")]
    private static extern byte* glGetString(uint name);
}
