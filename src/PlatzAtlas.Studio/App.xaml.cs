using System.IO;
using System.Windows;

namespace PlatzAtlas.Studio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var diagnosticsIndex = Array.IndexOf(args, "--diagnostics");
        var diagnosticsPath = diagnosticsIndex >= 0 && diagnosticsIndex + 1 < args.Length
            ? args[diagnosticsIndex + 1]
            : null;
        var projectIndex = Array.IndexOf(args, "--project");
        var projectPath = projectIndex >= 0 && projectIndex + 1 < args.Length
            ? args[projectIndex + 1]
            : null;
        try
        {
            var diagnostics = new StudioDiagnostics(diagnosticsPath);
            var window = new MainWindow(args.Contains("--cpu"), args.Contains("--simulate-device-loss"),
                args.Contains("--measure-frames") && diagnostics.Enabled, projectPath, diagnostics);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"Studio could not start. {error.Message}", "PLATZ ATLAS Studio",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
