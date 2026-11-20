using System.IO;
using System.Windows;
using PlatzAtlas.Rendering;
using PlatzAtlas.Runtime;

namespace PlatzAtlas.Player;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var renderer = ViewportArguments.ParseRenderer(e.Args);
            if (ViewportArguments.ReadOption(e.Args, "--check-room") is { } check)
            {
                RoomPackage.Open(check);
                Shutdown(0);
                return;
            }
            var window = new PlayerWindow(renderer, ViewportArguments.ReadOption(e.Args, "--room"),
                ViewportArguments.ReadOption(e.Args, "--diagnostics"));
            MainWindow = window;
            window.Show();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(error.Message);
            if (e.Args.Length == 0)
                MessageBox.Show(error.Message, "PLATZ Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }

    }
}
