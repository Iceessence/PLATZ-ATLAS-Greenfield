using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    public static readonly RoutedUICommand ExportRoomCommand = new("Export room", nameof(ExportRoomCommand), typeof(MainWindow));

    internal static void ExportSavedRoom(ProjectSession project, string path, bool overwrite = false) =>
        RoomPackage.Save(path, RuntimeRoom.FromProject(project.ReadSavedSnapshot()), overwrite);

    private void CanExportRoom(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _play is null && HasSavedRevision;

    private void ExportRoomExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_play is not null || !HasSavedRevision)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "Export saved room",
            DefaultExt = RoomPackage.Extension,
            Filter = "PLATZ Room (*.platzroom)|*.platzroom",
            FileName = Path.GetFileNameWithoutExtension(_session.FilePath) + RoomPackage.Extension,
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            ExportSavedRoom(_session, dialog.FileName, overwrite: true);
            ShowStatus($"Exported saved revision {_session.Current.Revision} to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            ShowStatus($"Room export failed. {error.Message}", error: true);
        }
    }

    private void UpdateSwitchBehaviorControls(LightSwitch? control)
    {
        SwitchBehaviorEditor.Visibility = control is null ? Visibility.Collapsed : Visibility.Visible;
        var currentBehavior = control?.Behavior ?? SwitchBehavior.Toggle;
        SwitchModeToggle.BorderBrush = (Brush)FindResource(currentBehavior == SwitchBehavior.Toggle ? "Accent" : "Border");
        SwitchModeTurnOn.BorderBrush = (Brush)FindResource(currentBehavior == SwitchBehavior.TurnOn ? "Accent" : "Border");
        SwitchModeToggle.IsEnabled = currentBehavior != SwitchBehavior.Toggle;
        SwitchModeTurnOn.IsEnabled = currentBehavior != SwitchBehavior.TurnOn;
    }

    private void SwitchModeClicked(object sender, RoutedEventArgs e)
    {
        if (_play is not null || Selection is not { } lantern || sender is not Button { Tag: string option } ||
            !Enum.TryParse<SwitchBehavior>(option, out var behavior))
            return;
        var project = _session.Current;
        var control = project.Switches.FirstOrDefault(item => item.TargetId == lantern.Id);
        if (control is not null && _session.Apply(new SetSwitchBehavior(project.ProjectId, project.Revision,
            control.Id, behavior)))
        {
            ShowStatus($"{control.Name} behavior set to {(behavior == SwitchBehavior.Toggle ? "Toggle" : "Turn on")}.");
            UpdateStudio();
        }
    }
}
