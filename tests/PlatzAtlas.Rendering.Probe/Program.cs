using System.Drawing;
using System.Runtime.InteropServices;
using PlatzAtlas.Rendering;

if (args is not [var capturePath])
{
    Console.Error.WriteLine("Pass an output path for the presented triangle PNG. Run on an unobstructed desktop.");
    return 2;
}

using var window = new Form
{
    Text = "PLATZ ATLAS Vulkan frame probe",
    ClientSize = new Size(280, 220),
    StartPosition = FormStartPosition.CenterScreen,
    TopMost = true
};
window.Show();
window.Activate();
Application.DoEvents();

var yellow = RenderColor.FromRgb(240, 180, 70);
var blue = RenderColor.FromRgb(14, 26, 44);
var green = RenderColor.FromRgb(34, 158, 95);
RenderTriangle[] triangles =
[
    new(new(.2f, .8f, yellow), new(.5f, .2f, yellow), new(.8f, .8f, yellow)),
    new(new(.4f, .65f, green), new(.5f, .44f, green), new(.6f, .65f, green))
];
var snapshot = new RenderSnapshot(new Guid("a84faf30-efe9-4c7a-956a-ed066a292cc8"), 0, blue, triangles);
using (var renderer = new VulkanViewport(window.Handle))
{
    ShowFrame(renderer, snapshot, window);
    Console.WriteLine(renderer.Diagnostics);
    window.ClientSize = new Size(340, 260);
    Application.DoEvents();
    ShowFrame(renderer, snapshot, window);

    using var pixels = new Bitmap(window.ClientSize.Width, window.ClientSize.Height);
    using (Graphics capture = Graphics.FromImage(pixels))
        capture.CopyFromScreen(window.PointToScreen(Point.Empty), Point.Empty, window.ClientSize);
    pixels.Save(capturePath, System.Drawing.Imaging.ImageFormat.Png);
    CheckPixel(pixels, .10, .10, Color.FromArgb(14, 26, 44));
    CheckPixel(pixels, .50, .30, Color.FromArgb(240, 180, 70));
    CheckPixel(pixels, .50, .55, Color.FromArgb(34, 158, 95));

    renderer.SimulateDeviceLoss();
    try
    {
        renderer.Present(snapshot, window.ClientSize.Width, window.ClientSize.Height);
        throw new InvalidOperationException("The simulated loss was ignored.");
    }
    catch (VulkanViewportException expected) when (expected.Message.Contains("simulated", StringComparison.Ordinal))
    {
        Console.WriteLine(expected.Message);
    }
}

using (var recreated = new VulkanViewport(window.Handle))
    ShowFrame(recreated, snapshot, window);
Console.WriteLine("PASS");
return 0;

static void ShowFrame(VulkanViewport renderer, RenderSnapshot snapshot, Form window)
{
    VulkanFrame frame = renderer.Present(snapshot, window.ClientSize.Width, window.ClientSize.Height);
    Console.WriteLine(frame);
    if (frame.Width != window.ClientSize.Width || frame.Height != window.ClientSize.Height)
        throw new InvalidOperationException("The presented frame size differs from the window client size.");
    Application.DoEvents();
    Marshal.ThrowExceptionForHR(DwmFlush());
}

static void CheckPixel(Bitmap pixels, double x, double y, Color expected)
{
    Color actual = pixels.GetPixel((int)(x * pixels.Width), (int)(y * pixels.Height));
    Console.WriteLine($"Pixel {x}, {y}: #{actual.R:x2}{actual.G:x2}{actual.B:x2} expected #{expected.R:x2}{expected.G:x2}{expected.B:x2}");
    if (Math.Abs(actual.R - expected.R) > 2 || Math.Abs(actual.G - expected.G) > 2 || Math.Abs(actual.B - expected.B) > 2)
        throw new InvalidOperationException($"Pixel {x}, {y} differs from the reference color by more than two channel units.");
}

[DllImport("dwmapi.dll")]
static extern int DwmFlush();
