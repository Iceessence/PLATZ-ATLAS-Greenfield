using System.IO;
using System.Windows;
using PlatzAtlas.Core;
using PlatzAtlas.Rendering;

namespace PlatzAtlas.Studio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var exporting = args.Contains("--export-room");
        try
        {
            var diagnosticsPath = ViewportArguments.ReadOption(args, "--diagnostics");
            var projectPath = ViewportArguments.ReadOption(args, "--project");
            if (exporting)
            {
                RunRoomExport(projectPath, ViewportArguments.ReadOption(args, "--export-room")!, diagnosticsPath);
                return;
            }
            var saveTargetPath = ViewportArguments.ReadOption(args, "--save-target");
            var preference = ViewportArguments.ParseRenderer(args);
            var diagnostics = new StudioDiagnostics(diagnosticsPath);
            var window = new MainWindow(preference, args.Contains("--simulate-device-loss"),
                args.Contains("--measure-frames") && diagnostics.Enabled, projectPath, saveTargetPath, diagnostics);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or ProjectEditException)
        {
            if (exporting)
                Console.Error.WriteLine(error.Message);
            else
                MessageBox.Show($"Studio could not start. {error.Message}", "PLATZ ATLAS Studio",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void RunRoomExport(string? projectPath, string exportPath, string? diagnosticsPath)
    {
        StudioDiagnostics? diagnostics = null;
        try
        {
            diagnostics = new StudioDiagnostics(diagnosticsPath);
            if (projectPath is null)
                throw new ArgumentException("Specify the saved project with --project.");
            var project = ProjectSession.Open(projectPath);
            global::PlatzAtlas.Studio.MainWindow.ExportSavedRoom(project, exportPath);
            diagnostics.Log("room-exported", new { project.Current.ProjectId, project.Current.Revision });
            Shutdown(0);
        }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            diagnostics?.Log("room-export-failed", new { error = error.Message });
            throw;
        }
    }
}
