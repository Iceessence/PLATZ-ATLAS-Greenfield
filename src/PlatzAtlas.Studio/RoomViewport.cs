using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PlatzAtlas.Rendering;

namespace PlatzAtlas.Studio;

internal sealed record ViewportFrame(
    string Label, string Detail, Guid ProjectId, long Revision, int Width, int Height,
    double DrawMilliseconds, double? GpuMilliseconds, double? EditToDrawMilliseconds,
    long PeakWorkingSetBytes, int? FixedCameraSample);

public sealed class RoomViewport : Grid
{
    private const double RoomAspect = 12d / 7;
    private RenderSnapshot? _snapshot;
    private long? _editStarted;
    private NativeSurface? _native;
    private VulkanViewport? _vulkan;
    private CpuPreview? _cpu;
    private bool _drawQueued;
    private bool _closed;
    private string? _fallbackReason;
    private int _sampleCount;

    public RoomViewport()
    {
        Loaded += (_, _) => QueueDraw();
        SizeChanged += (_, _) =>
        {
            if (_native is { } native)
                Fit(native);
            if (_cpu is { } cpu)
                Fit(cpu);
            QueueDraw();
        };
    }

    public bool UseCpu { get; set; }
    internal bool SimulateDeviceLoss { get; set; }
    internal bool MeasureFrames { get; set; }
    internal event Action<ViewportFrame>? FrameCompleted;
    internal event Action<double, double>? LanternClicked;

    internal void Show(RenderSnapshot snapshot, long? editStarted)
    {
        _snapshot = snapshot;
        _editStarted = editStarted;
        QueueDraw();
    }

    internal void Shutdown()
    {
        _closed = true;
        ReleaseNative();
    }

    internal void Refresh() => QueueDraw();

    private void QueueDraw()
    {
        if (_drawQueued || !IsLoaded || _closed)
            return;

        _drawQueued = true;
        Dispatcher.InvokeAsync(Draw, DispatcherPriority.Render);
    }

    private void Draw()
    {
        _drawQueued = false;
        if (_closed || !IsLoaded || _snapshot is not { } snapshot || ActualWidth < 1 || ActualHeight < 1
            || Window.GetWindow(this)?.WindowState == WindowState.Minimized)
            return;

        if (UseCpu || _fallbackReason is not null)
        {
            DrawCpu(snapshot);
            return;
        }

        try
        {
            if (_native is null)
            {
                _native = new NativeSurface(QueueDraw, ReleaseVulkan, (x, y) => LanternClicked?.Invoke(x, y));
                Fit(_native);
                Children.Add(_native);
                return;
            }

            var window = _native.Handle;
            var size = _native.PixelSize;
            if (window == 0 || size.Width == 0 || size.Height == 0)
                return;

            _vulkan ??= new VulkanViewport(window);
            var frame = _vulkan.Present(snapshot, size.Width, size.Height);
            Report("Vulkan", _vulkan.Diagnostics, snapshot,
                frame.Width, frame.Height, frame.ElapsedMilliseconds, frame.GpuMilliseconds);
            if (SimulateDeviceLoss)
            {
                SimulateDeviceLoss = false;
                _vulkan.SimulateDeviceLoss();
                QueueDraw();
            }
        }
        catch (Exception error) when (error is VulkanViewportException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException or System.ComponentModel.Win32Exception)
        {
            _fallbackReason = error.Message;
            ReleaseNative();
            DrawCpu(snapshot);
        }
    }

    private void DrawCpu(RenderSnapshot snapshot)
    {
        if (_cpu is null)
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;

            _cpu = new CpuPreview(OnCpuDrawn);
            Fit(_cpu);
            RenderOptions.SetEdgeMode(_cpu, EdgeMode.Aliased);
            _cpu.MouseLeftButtonDown += (_, e) =>
            {
                var point = e.GetPosition(_cpu);
                if (_cpu.ActualWidth > 0 && _cpu.ActualHeight > 0)
                    LanternClicked?.Invoke(point.X / _cpu.ActualWidth, point.Y / _cpu.ActualHeight);
            };
            Children.Add(_cpu);
        }
        _cpu.Snapshot = snapshot;
        _cpu.InvalidateVisual();
    }

    private void Fit(FrameworkElement surface)
    {
        surface.HorizontalAlignment = HorizontalAlignment.Center;
        surface.VerticalAlignment = VerticalAlignment.Center;
        surface.Width = Math.Min(ActualWidth, ActualHeight * RoomAspect);
        surface.Height = Math.Min(ActualHeight, ActualWidth / RoomAspect);
    }

    private void OnCpuDrawn(RenderSnapshot snapshot, int width, int height, double elapsed)
    {
        if (!_closed && ReferenceEquals(_snapshot, snapshot))
            Report("CPU Reference", _fallbackReason is null ? "Software preview selected."
                : $"Vulkan unavailable. {_fallbackReason}", snapshot, width, height, elapsed);
    }

    private void Report(string label, string detail, RenderSnapshot snapshot, int width, int height,
        double elapsed, double? gpuElapsed = null)
    {
        double? editTime = _editStarted is long started ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : null;
        _editStarted = null;
        using var process = Process.GetCurrentProcess();
        // First ten frames warm the fixed camera workload. Keep each draw on the ordinary UI dispatcher.
        int? sample = MeasureFrames && _sampleCount < 130 ? ++_sampleCount : null;
        FrameCompleted?.Invoke(new ViewportFrame(label, detail, snapshot.ProjectId, snapshot.Revision,
            width, height, elapsed, gpuElapsed, editTime, process.PeakWorkingSet64, sample));
        if (sample is < 130)
            Dispatcher.InvokeAsync(QueueDraw, DispatcherPriority.Background);
    }

    private void ReleaseVulkan()
    {
        _vulkan?.Dispose();
        _vulkan = null;
    }

    private void ReleaseNative()
    {
        ReleaseVulkan();
        if (_native is { } native)
        {
            _native = null;
            Children.Remove(native);
            native.Dispose();
        }
    }

    private sealed class CpuPreview(Action<RenderSnapshot, int, int, double> completed) : FrameworkElement
    {
        private readonly Dictionary<RenderColor, SolidColorBrush> _brushes = new();
        public RenderSnapshot? Snapshot { get; set; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (Snapshot is not { } snapshot)
                return;

            var started = Stopwatch.GetTimestamp();
            var width = ActualWidth;
            var height = ActualHeight;
            drawingContext.DrawRectangle(BrushFor(snapshot.Background), null, new Rect(0, 0, width, height));
            foreach (var triangle in snapshot.Triangles)
            {
                var geometry = new StreamGeometry();
                using (var figure = geometry.Open())
                {
                    figure.BeginFigure(PointFor(triangle.A), isFilled: true, isClosed: true);
                    figure.LineTo(PointFor(triangle.B), isStroked: false, isSmoothJoin: false);
                    figure.LineTo(PointFor(triangle.C), isStroked: false, isSmoothJoin: false);
                }
                geometry.Freeze();
                drawingContext.DrawGeometry(BrushFor(triangle.A.Color), null, geometry);
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var dpi = VisualTreeHelper.GetDpi(this);
            Dispatcher.InvokeAsync(() => completed(snapshot, (int)Math.Round(width * dpi.DpiScaleX),
                (int)Math.Round(height * dpi.DpiScaleY), elapsed), DispatcherPriority.Loaded);

            Point PointFor(RenderVertex vertex) => new(vertex.X * width, vertex.Y * height);
        }

        private SolidColorBrush BrushFor(RenderColor color)
        {
            if (_brushes.TryGetValue(color, out var brush))
                return brush;

            brush = new SolidColorBrush(Color.FromRgb(ToByte(color.R), ToByte(color.G), ToByte(color.B)));
            brush.Freeze();
            _brushes.Add(color, brush);
            return brush;
        }

        private static byte ToByte(float channel) => (byte)Math.Round(Math.Clamp(channel, 0, 1) * 255);
    }

    private sealed class NativeSurface(Action changed, Action closing, Action<double, double> clicked) : HwndHost
    {
        public (int Width, int Height) PixelSize => Handle != 0 && GetClientRect(Handle, out var rect)
            ? (Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top))
            : (0, 0);

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            const uint childVisible = 0x50000000;
            const uint clipSiblings = 0x04000000;
            const uint notify = 0x100;
            var hwnd = CreateWindowEx(0, "static", "", childVisible | clipSiblings | notify,
                0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
            if (hwnd == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            changed();
            return new HandleRef(this, hwnd);
        }

        protected override void OnWindowPositionChanged(Rect rect)
        {
            base.OnWindowPositionChanged(rect);
            changed();
        }

        protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            const int click = 0x0201;
            if (message == click && PixelSize is (> 0, > 0) size)
            {
                var x = unchecked((short)((long)lParam & 0xffff));
                var y = unchecked((short)(((long)lParam >> 16) & 0xffff));
                clicked((double)x / size.Width, (double)y / size.Height);
                handled = true;
            }
            return base.WndProc(hwnd, message, wParam, lParam, ref handled);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            closing();
            DestroyWindow(hwnd.Handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(uint extraStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(nint hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint hwnd);
    }
}
