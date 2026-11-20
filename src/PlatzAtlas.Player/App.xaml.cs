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
            if (Option("--check-room") is { } check)
            {
                RoomPackage.Open(check);
                Shutdown(0);
                return;
            }
            var renderer = e.Args.Contains("--cpu") ? RendererPreference.CpuReference :
                e.Args.Contains("--opengl") ? RendererPreference.OpenGl :
                e.Args.Contains("--vulkan") ? RendererPreference.Vulkan : RendererPreference.Auto;
            var window = new PlayerWindow(renderer, Option("--room"), Option("--diagnostics"));
            MainWindow = window;
            window.Show();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(error.Message);
            if (!e.Args.Contains("--check-room"))
                MessageBox.Show(error.Message, "PLATZ Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }

        string? Option(string key)
        {
            var index = Array.IndexOf(e.Args, key);
            if (index < 0)
                return null;
            if (index + 1 == e.Args.Length || e.Args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing filename after {key}.");
            return e.Args[index + 1];
        }
    }
}
