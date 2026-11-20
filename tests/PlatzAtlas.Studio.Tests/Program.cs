using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PlatzAtlas.Studio;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        try
        {
            PixelInput();
            if (args is [var evidenceDirectory])
            {
                var folder = Directory.CreateDirectory(evidenceDirectory).FullName;
                WorkingRoom(folder);
                PresetsInStudio(folder);
                AnimateRoom(folder);
                PlanInStudio(folder);
                WorkspaceUxChecks(folder);
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { app.Shutdown(); }
    }

    private static void PixelInput()
    {
        var window = new PixelEditorWindow(null);
        window.Show();
        try
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var canvas = Find<Grid>(window, "PixelSurface");
            Start(30, 30);
            window.ContinuePointerStroke(new Point(65, 30));
            Click(window, "PixelUndo");
            window.ContinuePointerStroke(new Point(115, 30));
            Check(!canvas.IsMouseCaptured && !Opaque(2, 2) && !Opaque(9, 2), "Undo ends capture and discards only the interrupted stroke");
            Click(window, "PixelRedo");
            Check(Opaque(2, 2) && Opaque(5, 2) && !Opaque(9, 2), "Redo restores exactly the interrupted stroke");

            Start(30, 64);
            Click(window, "PixelRedo");
            window.ContinuePointerStroke(new Point(115, 64));
            Check(!canvas.IsMouseCaptured && Opaque(2, 5) && !Opaque(9, 5), "Redo finalizes the new stroke and releases capture");

            Start(30, 96);
            Click(window, "Pixels16");
            window.ContinuePointerStroke(new Point(115, 96));
            Check(!canvas.IsMouseCaptured && Image().PixelWidth == 16, "Resize finalizes capture before changing the canvas");
            Click(window, "Pixels32");
            Start(30, 144);
            Check(window.HandleCanvasKey(Key.Space) && canvas.IsMouseCaptured, "Space is handled while the pointer owns the stroke");
            window.ContinuePointerStroke(new Point(65, 144));
            canvas.ReleaseMouseCapture();
            Check(Opaque(5, 12), "The original stroke continues and commits at capture loss");

            Find<TextBox>(window, "PixelColor").Text = "ZZZZ";
            Check(window.HandleCanvasKey(Key.Space), "Invalid hex still consumes Space");
            Check(Find<TextBlock>(window, "PixelStatus").Text.Contains("Use eight hex digits", StringComparison.Ordinal),
                "The invalid color error remains visible");
            Console.WriteLine("PASS WPF Pixel Editor capture, Undo, Redo, Resize, Space and visible hex validation");

            void Start(int x, int y)
            {
                Check(window.BeginPointerStroke(new Point(x, y)) && canvas.IsMouseCaptured, "The shown canvas owns WPF mouse capture");
            }

            BitmapSource Image() => (BitmapSource)Find<Image>(window, "PixelImage").Source;
            bool Opaque(int x, int y)
            {
                var bytes = new byte[4];
                Image().CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
                return bytes[3] != 0;
            }
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Window window, string name) where T : FrameworkElement => (T)window.FindName(name);
    private static void Click(Window window, string name) =>
        Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
