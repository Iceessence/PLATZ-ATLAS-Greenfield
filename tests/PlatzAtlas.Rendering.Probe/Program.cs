using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PlatzAtlas.Rendering;

if (args.Length < 1 || args.Length > 2)
{
    Console.Error.WriteLine("Pass an output PNG path for the Vulkan frame capture (opengl-probe.png is written beside it).");
    return 2;
}

string vulkanCapturePath = Path.GetFullPath(args[0]);
bool requireValidation = args.Contains("--require-validation", StringComparer.Ordinal);
string openglCapturePath = Path.Combine(Path.GetDirectoryName(vulkanCapturePath)!, "opengl-probe.png");

Environment.SetEnvironmentVariable("PLATZ_ATLAS_VK_VALIDATION", "1");

var yellow = RenderColor.FromRgb(240, 180, 70);
var blue = RenderColor.FromRgb(14, 26, 44);
var green = RenderColor.FromRgb(34, 158, 95);
RenderTriangle[] triangles =
[
    new(new(.2f, .8f, yellow), new(.5f, .2f, yellow), new(.8f, .8f, yellow)),
    new(new(.4f, .65f, green), new(.5f, .44f, green), new(.6f, .65f, green))
];
var id = new Guid("a84faf30-efe9-4c7a-956a-ed066a292cc8");
var blank = new RenderSnapshot(id, 0, blue, triangles);
var image = new RasterTexture(2, 2, new byte[]
{
    224, 81, 39, 255, 255, 0, 255, 0,
    38, 154, 210, 128, 60, 205, 162, 255
});
var snapshot = new RenderSnapshot(id, 1, blue, triangles,
    sprites: [new RenderSprite(image, new(.64f, .10f), new(.94f, .10f), new(.94f, .40f), new(.64f, .40f))]);

byte[] vkPixels;
using (var window = CreateProbeWindow("PLATZ ATLAS Vulkan frame probe"))
{
    VulkanViewport? renderer = null;
    using (renderer = new VulkanViewport(window.Handle))
    {
        if (requireValidation && !renderer.ValidationEnabled)
            throw new InvalidOperationException("Khronos validation layer was required but not enabled.");

        ShowVulkanFrame(renderer, blank, window);
        Console.WriteLine(renderer.Diagnostics);

        window.ClientSize = new Size(340, 260);
        Application.DoEvents();
        vkPixels = new byte[window.ClientSize.Width * window.ClientSize.Height * 4];
        ShowVulkanFrame(renderer, snapshot, window, vkPixels);

        using (var bitmap = BitmapFromBgra(vkPixels, window.ClientSize.Width, window.ClientSize.Height))
        {
            bitmap.Save(vulkanCapturePath, ImageFormat.Png);
            CheckPixel(bitmap, .10, .10, Color.FromArgb(14, 26, 44), "Vulkan");
            CheckPixel(bitmap, .50, .30, Color.FromArgb(240, 180, 70), "Vulkan");
            CheckPixel(bitmap, .50, .55, Color.FromArgb(34, 158, 95), "Vulkan");
            CheckTexture(bitmap, "Vulkan");
        }

        renderer.SimulateDeviceLoss();
        try
        {
            renderer.Present(snapshot, window.ClientSize.Width, window.ClientSize.Height);
            throw new InvalidOperationException("The simulated Vulkan loss was ignored.");
        }
        catch (VulkanViewportException expected) when (expected.Message.Contains("simulated", StringComparison.Ordinal))
        {
            Console.WriteLine(expected.Message);
        }
    }

    AssertZeroValidationIssues(renderer, "initial Vulkan lifecycle");

    using var recreated = new VulkanViewport(window.Handle);
    ShowVulkanFrame(recreated, snapshot, window);
    recreated.Dispose();
    AssertZeroValidationIssues(recreated, "recreated Vulkan lifecycle");
}

byte[] glPixels;
using (var glWindow = CreateProbeWindow("PLATZ ATLAS OpenGL frame probe"))
{
    using (var glRenderer = new OpenGlViewport(glWindow.Handle))
    {
        ShowOpenGlFrame(glRenderer, blank, glWindow);
        Console.WriteLine(glRenderer.Diagnostics);

        glWindow.ClientSize = new Size(340, 260);
        Application.DoEvents();
        glPixels = new byte[glWindow.ClientSize.Width * glWindow.ClientSize.Height * 4];
        ShowOpenGlFrame(glRenderer, snapshot, glWindow, glPixels);

        using (var glBitmap = BitmapFromBgra(glPixels, glWindow.ClientSize.Width, glWindow.ClientSize.Height))
        {
            glBitmap.Save(openglCapturePath, ImageFormat.Png);
            CheckPixel(glBitmap, .10, .10, Color.FromArgb(14, 26, 44), "OpenGL");
            CheckPixel(glBitmap, .50, .30, Color.FromArgb(240, 180, 70), "OpenGL");
            CheckPixel(glBitmap, .50, .55, Color.FromArgb(34, 158, 95), "OpenGL");
            CheckTexture(glBitmap, "OpenGL");
        }

        glRenderer.SimulateDeviceLoss();
        try
        {
            glRenderer.Present(snapshot, glWindow.ClientSize.Width, glWindow.ClientSize.Height);
            throw new InvalidOperationException("The simulated OpenGL loss was ignored.");
        }
        catch (OpenGlViewportException expected) when (expected.Message.Contains("simulated", StringComparison.Ordinal))
        {
            Console.WriteLine(expected.Message);
        }
    }

    using var glRecoveryWindow = CreateProbeWindow("PLATZ ATLAS OpenGL recovery probe");
    glRecoveryWindow.ClientSize = new Size(340, 260);
    Application.DoEvents();
    using var recreatedGl = new OpenGlViewport(glRecoveryWindow.Handle);
    ShowOpenGlFrame(recreatedGl, snapshot, glRecoveryWindow);
}

CompareSampledPixels(vkPixels, glPixels, 340, 260);
Console.WriteLine("PASS");
return 0;

static Form CreateProbeWindow(string title)
{
    var window = new Form
    {
        Text = title,
        ClientSize = new Size(280, 220),
        StartPosition = FormStartPosition.CenterScreen
    };
    window.Show();
    Application.DoEvents();
    return window;
}

static void ShowVulkanFrame(VulkanViewport renderer, RenderSnapshot snapshot, Form window, Span<byte> readback = default)
{
    VulkanFrame frame = renderer.Present(snapshot, window.ClientSize.Width, window.ClientSize.Height, readback);
    Console.WriteLine($"Vulkan: {frame}");
    if (frame.Width != window.ClientSize.Width || frame.Height != window.ClientSize.Height)
        throw new InvalidOperationException("The presented Vulkan frame size differs from the window client size.");
    Application.DoEvents();
}

static void ShowOpenGlFrame(OpenGlViewport renderer, RenderSnapshot snapshot, Form window, Span<byte> readback = default)
{
    VulkanFrame frame = renderer.Present(snapshot, window.ClientSize.Width, window.ClientSize.Height, readback);
    Console.WriteLine($"OpenGL: {frame}");
    if (frame.Width != window.ClientSize.Width || frame.Height != window.ClientSize.Height)
        throw new InvalidOperationException("The presented OpenGL frame size differs from the window client size.");
    Application.DoEvents();
}

static Bitmap BitmapFromBgra(byte[] bgra, int width, int height)
{
    var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    try
    {
        for (int y = 0; y < height; y++)
            Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
    }
    finally
    {
        bitmap.UnlockBits(data);
    }
    return bitmap;
}

static void CheckPixel(Bitmap pixels, double x, double y, Color expected, string backend)
{
    Color actual = pixels.GetPixel((int)(x * pixels.Width), (int)(y * pixels.Height));
    Console.WriteLine($"{backend} pixel {x}, {y}: #{actual.R:x2}{actual.G:x2}{actual.B:x2}{actual.A:x2} expected #{expected.R:x2}{expected.G:x2}{expected.B:x2}{expected.A:x2}");
    if (Math.Abs(actual.R - expected.R) > 2 || Math.Abs(actual.G - expected.G) > 2 ||
        Math.Abs(actual.B - expected.B) > 2 || actual.A != expected.A)
        throw new InvalidOperationException($"{backend} pixel {x}, {y} differs from the reference color by more than two channel units.");
}

static void CompareSampledPixels(byte[] vkBgra, byte[] glBgra, int width, int height)
{
    foreach ((double x, double y) in new[] { (.10, .10), (.50, .30), (.50, .55), (.25, .75), (.75, .75),
        (.70, .16), (.88, .16), (.70, .34), (.88, .34) })
    {
        int offset = (((int)(y * height) * width) + (int)(x * width)) * 4;
        int db = Math.Abs(vkBgra[offset] - glBgra[offset]);
        int dg = Math.Abs(vkBgra[offset + 1] - glBgra[offset + 1]);
        int dr = Math.Abs(vkBgra[offset + 2] - glBgra[offset + 2]);
        int da = Math.Abs(vkBgra[offset + 3] - glBgra[offset + 3]);
        if (db > 2 || dg > 2 || dr > 2 || da != 0)
            throw new InvalidOperationException($"Vulkan vs OpenGL mismatch at ({x}, {y}): delta BGRA=({db},{dg},{dr},{da}).");
    }
}

static void CheckTexture(Bitmap bitmap, string backend)
{
    CheckPixel(bitmap, .70, .16, Color.FromArgb(224, 81, 39), backend);
    CheckPixel(bitmap, .88, .16, Color.FromArgb(14, 26, 44), backend);
    CheckPixel(bitmap, .70, .34, Color.FromArgb(26, 90, 127), backend);
    CheckPixel(bitmap, .88, .34, Color.FromArgb(60, 205, 162), backend);
}

static void AssertZeroValidationIssues(VulkanViewport viewport, string phase)
{
    Console.WriteLine($"Validation ({phase}): enabled={viewport.ValidationEnabled} errors={viewport.ValidationErrorCount} warnings={viewport.ValidationWarningCount}");
    foreach (string message in viewport.ValidationMessages)
        Console.WriteLine($"  {message}");
    if (viewport.ValidationErrorCount != 0 || viewport.ValidationWarningCount != 0)
        throw new InvalidOperationException($"Vulkan validation reported errors or warnings during {phase}.");
}
