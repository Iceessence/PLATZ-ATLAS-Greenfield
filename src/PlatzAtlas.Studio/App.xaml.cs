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
        try
        {
            var diagnosticsPath = ReadOption(args, "--diagnostics");
            var projectPath = ReadOption(args, "--project");
            var saveTargetPath = ReadOption(args, "--save-target");
            var exportPath = ReadOption(args, "--export-room");
            if (exportPath is not null)
            {
                RunRoomExport(projectPath, exportPath, diagnosticsPath);
                return;
            }
            var preference = ParseRendererPreference(args);
            var diagnostics = new StudioDiagnostics(diagnosticsPath);
            var window = new MainWindow(preference, args.Contains("--simulate-device-loss"),
                args.Contains("--measure-frames") && diagnostics.Enabled, projectPath, saveTargetPath, diagnostics);
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

    private void RunRoomExport(string? projectPath, string exportPath, string? diagnosticsPath)
    {
        var diagnostics = new StudioDiagnostics(diagnosticsPath);
        try
        {
            if (projectPath is null)
                throw new ArgumentException("Specify the saved project with --project.");
            var project = ProjectSession.Open(projectPath);
            global::PlatzAtlas.Studio.MainWindow.ExportSavedRoom(project, exportPath);
            diagnostics.Log("room-exported", new { project.Current.ProjectId, project.Current.Revision });
            Shutdown(0);
        }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            diagnostics.Log("room-export-failed", new { error = error.Message });
            Console.Error.WriteLine(error.Message);
            Shutdown(1);
        }
    }

    private static string? ReadOption(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        if (index < 0)
            return null;
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value after {flag}.");
        return args[index + 1];
    }

    private static RendererPreference ParseRendererPreference(string[] args)
    {
        if (ReadOption(args, "--renderer") is { } value)
        {
            return value.ToLowerInvariant() switch
            {
                "auto" => RendererPreference.Auto,
                "vulkan" => RendererPreference.Vulkan,
                "opengl" or "gl" => RendererPreference.OpenGl,
                "cpu" or "cpureference" or "cpu-reference" => RendererPreference.CpuReference,
                _ => throw new ArgumentException($"Unsupported renderer preference \"{value}\". Use auto, vulkan, opengl, or cpu.")
            };
        }

        if (args.Contains("--cpu"))
            return RendererPreference.CpuReference;
        if (args.Contains("--opengl"))
            return RendererPreference.OpenGl;
        if (args.Contains("--vulkan"))
            return RendererPreference.Vulkan;
        return RendererPreference.Auto;
    }
}
