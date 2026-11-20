using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.RoomView;

namespace PlatzAtlas.Studio;

public partial class MainWindow : Window
{
    private readonly StudioDiagnostics _diagnostics;
    private readonly string? _saveTargetPath;
    private ProjectSession _session;
    private long _openedRevision;
    private bool _changingProject;

    internal MainWindow(RendererPreference rendererPreference, bool simulateDeviceLoss, bool measureFrames,
        string? projectPath, string? saveTargetPath, StudioDiagnostics diagnostics)
    {
        _diagnostics = diagnostics;
        _saveTargetPath = saveTargetPath;
        _session = ProjectSession.NewMuseum();
        InitializeComponent();
        InitializeAtlas();
        Viewport.SetPreference(rendererPreference);
        Viewport.SimulateDeviceLoss = simulateDeviceLoss;
        Viewport.MeasureFrames = measureFrames;
        Viewport.FrameCompleted += FrameCompleted;
        Viewport.VulkanValidated += summary => _diagnostics.Log("vulkan-validation", summary);
        Viewport.LanternClicked += SelectAt;
        StateChanged += (_, _) => Viewport.Refresh();

        UpdateRendererButtons();
        SetSession(_session, "Select a lantern in the scene to inspect its light.");
        if (projectPath is not null)
            OpenProject(projectPath);

        if (_diagnostics.Enabled)
            ContentRendered += (_, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                using var process = Process.GetCurrentProcess();
                _diagnostics.Log("window-rendered", new
                {
                    dpi = dpi.PixelsPerInchX,
                    width = ActualWidth,
                    height = ActualHeight,
                    workingSetBytes = process.WorkingSet64
                });
            };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
            e.Cancel = !ConfirmLeave();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPlay("window-closed");
        _atlasHost.Revoke();
        Viewport.Shutdown();
        base.OnClosed(e);
    }

    private Lantern? Selection => Objects.SelectedItem is SceneObjectItem item
        ? _session.Current.Objects.SingleOrDefault(lantern => lantern.Id == item.Id)
        : null;

    private void SetSession(ProjectSession session, string message)
    {
        _changingProject = true;
        _session = session;
        ResetAtlasSession();
        _openedRevision = session.Current.Revision;
        Objects.ItemsSource = session.Current.Objects.OrderBy(lantern => lantern.X)
            .Select(lantern => new SceneObjectItem(lantern.Id, lantern.Name, lantern.Location)).ToArray();
        Objects.SelectedIndex = 0;
        _changingProject = false;
        ShowStatus(message);
        UpdateStudio();
    }

    private void NewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_play is null && ConfirmLeave())
            SetSession(ProjectSession.NewMuseum(), "New museum created. Save to choose a project file.");
    }

    private void OpenExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_play is not null || !ConfirmLeave())
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Open a museum project",
            Filter = "PLATZ project (*.platz)|*.platz|Recover previous (*.platz.previous)|*.platz.previous",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            OpenProject(dialog.FileName);
    }

    private void OpenProject(string path)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            const string previous = ".previous";
            var recovery = path.EndsWith(previous, StringComparison.OrdinalIgnoreCase);
            var opened = recovery ? ProjectSession.OpenPrevious(path[..^previous.Length]) : ProjectSession.Open(path);
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            SetSession(opened, recovery
                ? $"Recovered revision {opened.Current.Revision}. Save to a new project file."
                : opened.FilePath is null
                    ? "Opened a copy of the public museum. Save to choose a new project file."
                    : $"Opened {Path.GetFileName(path)}.");
            _diagnostics.Log("project-open", new { revision = opened.Current.Revision, recovery, durationMs = duration });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException)
        {
            ShowStatus($"Could not open {Path.GetFileName(path)}. {error.Message}", error: true);
            _diagnostics.Log("project-open-failed", new { error = error.GetType().Name });
        }
    }

    private void SaveExecuted(object sender, ExecutedRoutedEventArgs e) => SaveProject();

    private void SaveAsExecuted(object sender, ExecutedRoutedEventArgs e) => SaveProject(saveAs: true);

    private bool SaveProject(bool saveAs = false)
    {
        if (_play is not null)
            return false;

        var path = _session.FilePath;
        var chosePath = saveAs || path is null;
        if (chosePath)
        {
            if (_saveTargetPath is not null)
            {
                path = _saveTargetPath;
            }
            else
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save museum project",
                    Filter = "PLATZ project (*.platz)|*.platz",
                    DefaultExt = ".platz",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = path is not null ? Path.GetFileName(path) : "Night Museum.platz"
                };
                if (dialog.ShowDialog(this) != true)
                    return false;
                path = dialog.FileName;
            }
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            _session.Save(path, overwriteExisting: chosePath);
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            ShowStatus($"Saved revision {_session.Current.Revision} to {Path.GetFileName(path)}.");
            UpdateRevision();
            CommandManager.InvalidateRequerySuggested();
            _diagnostics.Log("project-save", new { revision = _session.Current.Revision, durationMs = duration });
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowStatus($"Could not save. {error.Message} Your edits are still open. Try Save or Save as again.",
                error: true);
            _diagnostics.Log("project-save-failed", new { revision = _session.Current.Revision, error = error.GetType().Name });
            return false;
        }
    }

    private bool ConfirmLeave()
    {
        if (!_session.IsDirty || (_session.FilePath is null && _session.Current.Revision == _openedRevision))
            return true;

        var answer = MessageBox.Show(this, $"Save changes to {_session.Current.Name}?", "Unsaved museum",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && SaveProject());
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingProject)
            UpdateStudio();
    }

    private void SelectAt(double x, double y)
    {
        if (_play is not null)
            return;

        var lantern = _session.Current.Objects.Reverse().FirstOrDefault(candidate =>
            Math.Abs(candidate.X - x) <= 0.07 && y >= candidate.Y - 0.17 && y <= candidate.Y + 0.13);
        if (lantern is not null)
            Objects.SelectedItem = Objects.Items.OfType<SceneObjectItem>().Single(item => item.Id == lantern.Id);
    }

    private void ToggleLightClicked(object sender, RoutedEventArgs e)
    {
        if (_play is not null)
            return;

        var started = Stopwatch.GetTimestamp();
        var project = _session.Current;
        if (Selection is not { } lantern)
            return;

        var control = project.Switches.FirstOrDefault(item => item.TargetId == lantern.Id);
        try
        {
            if (_session.Apply(new SetLight(project.ProjectId, project.Revision, lantern.Id, !lantern.IsOn, control?.Id)))
            {
                ShowStatus($"{lantern.Location} light turned {(lantern.IsOn ? "off" : "on")}.");
                _diagnostics.Log("light-edit", new { objectId = lantern.Id, revision = _session.Current.Revision, isOn = !lantern.IsOn });
                UpdateStudio(started);
            }
        }
        catch (ProjectEditException error)
        {
            ShowStatus(error.Message, error: true);
        }
    }

    private void CanAuthor(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _play is null;

    private void CanUndo(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _play is null && _session.CanUndo;

    private void CanRedo(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _play is null && _session.CanRedo;

    private void UndoExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var started = Stopwatch.GetTimestamp();
        if (_play is null && _session.Undo())
        {
            ShowStatus("Undid the last scene change.");
            UpdateStudio(started);
        }
    }

    private void RedoExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var started = Stopwatch.GetTimestamp();
        if (_play is null && _session.Redo())
        {
            ShowStatus("Redid the last scene change.");
            UpdateStudio(started);
        }
    }

    private void UpdateStudio(long? editStarted = null)
    {
        var project = _session.Current;
        var selected = Selection;
        var control = project.Switches.FirstOrDefault(item => item.TargetId == selected?.Id);
        RoomTitle.Text = project.Name;
        ObjectCount.Text = $"{project.Objects.Count} lanterns";
        SelectedName.Text = selected?.Name ?? "Select a lantern";
        SelectedLocation.Text = selected?.Location ?? string.Empty;
        ProjectId.Text = project.ProjectId.ToString();
        ObjectId.Text = selected?.Id.ToString() ?? "—";
        SwitchHeading.Text = control?.Name ?? "Light control";
        UpdateSwitchBehaviorControls(control);
        SwitchTarget.Text = selected is null ? "Choose an object in the scene."
            : $"{(control is null ? "Light" : $"{selected.Location} lantern")} · {(selected.IsOn ? "On" : "Off")}";
        ToggleLabel.Text = selected?.IsOn == true ? "Turn light off" : "Turn light on";
        AutomationProperties.SetName(ToggleLight, selected is null ? "Turn light on"
            : $"Turn {selected.Location} light {(selected.IsOn ? "off" : "on")}");
        ToggleLight.IsEnabled = _play is null && selected is not null;
        UpdateRevision();
        UpdateScenePresentation(editStarted);
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateScenePresentation(long? editStarted = null)
    {
        var project = _session.Current;
        var play = _play?.Current;
        var selection = play is null ? Selection?.Id : _lastPlaySwitch?.TargetId;
        var preview = _atlasPreview;
        AtlasPreviewBanner.Visibility = preview is null ? Visibility.Collapsed : Visibility.Visible;
        AtlasPreviewLabel.Text = preview is null ? "" :
            $"Preview · {preview.Before.Location} · {LightState(preview.Before.IsOn)} → {LightState(preview.After.IsOn)} · Saved r{preview.Operation.BaseRevision}";
        AtlasPreviewStart.IsEnabled = preview is null;
        AtlasPreviewStart.Visibility = preview is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateAtlasCommitControls();
        UpdatePlayControls();
        AutomationProperties.SetName(Viewport, play is not null ? PlayBannerText.Text
            : preview is null ? "Museum room preview" : AtlasPreviewLabel.Text);
        var ordered = (play?.Objects ?? project.Objects).OrderBy(lantern => lantern.X).ToArray();
        UpdatePreviewCard(WestCard, WestLabel, WestState, ordered.FirstOrDefault(), selection);
        UpdatePreviewCard(EastCard, EastLabel, EastState, ordered.Skip(1).FirstOrDefault(), selection);
        Viewport.Show(MuseumArt.Create(project, selection, preview, play), editStarted);
    }

    private void UpdateRevision()
    {
        var saved = _session.SavedRevision is long revision ? $"Saved r{revision}" : "Unsaved";
        var dirty = _session.IsDirty && _session.SavedRevision.HasValue ? " · Unsaved changes" : "";
        RevisionLabel.Text = $"Revision {_session.Current.Revision} · Original r{MuseumProject.InitialRevision} · {saved}{dirty}";
        RevisionLabel.ToolTip = _session.FilePath ?? "Choose a file with Save";
        UpdateAtlasAccess();
        PlayToggle.ToolTip = _play is not null ? "Stop Play (F5)"
            : CanStartPlay ? "Play the saved revision (F5)" : "Save the current revision to start Play.";
    }

    private void UpdatePreviewCard(Border card, TextBlock location, TextBlock state, Lantern? lantern, Guid? selection)
    {
        card.Visibility = lantern is null ? Visibility.Collapsed : Visibility.Visible;
        if (lantern is null)
            return;
        location.Text = lantern.Location;
        if (_atlasPreview is { } preview && preview.After.Id == lantern.Id)
        {
            state.Text = $"Preview {LightState(preview.After.IsOn).ToLowerInvariant()} · Saved {LightState(lantern.IsOn).ToLowerInvariant()}";
            state.Foreground = (Brush)FindResource("PreviewAccent");
            card.BorderBrush = state.Foreground;
            return;
        }
        var selected = lantern.Id == selection;
        var marker = _play is null ? "Selected" : "Last used";
        var highlightBrush = _play is null ? "Accent" : "PlayAccent";
        state.Text = $"Light {(lantern.IsOn ? "on" : "off")}{(selected ? $" · {marker}" : "")}";
        state.Foreground = (Brush)FindResource(lantern.IsOn ? "Accent" : "Muted");
        card.BorderBrush = (Brush)FindResource(selected ? highlightBrush : "Border");
    }

    private void ShowStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(error ? "Error" : "Muted");
        UIElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void FrameCompleted(ViewportFrame frame)
    {
        BackendText.Text = frame.Label;
        BackendText.ToolTip = frame.Detail;
        AutomationProperties.SetName(BackendText, $"{frame.Label}. {frame.Detail}");
        _diagnostics.Log("frame", frame);
    }

    private void RendererClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<RendererPreference>(tag, out var preference))
            return;

        Viewport.SetPreference(preference);
        UpdateRendererButtons();
        string display = preference switch
        {
            RendererPreference.Auto => "Auto",
            RendererPreference.Vulkan => "Vulkan",
            RendererPreference.OpenGl => "OpenGL",
            RendererPreference.CpuReference => "CPU Reference",
            _ => preference.ToString()
        };
        ShowStatus($"Renderer preference set to {display}.");
        _diagnostics.Log("renderer-changed", new { preference = preference.ToString() });
    }

    private void UpdateRendererButtons()
    {
        var active = Viewport.Preference;
        UpdateRendererButton(RendererAuto, RendererPreference.Auto, active);
        UpdateRendererButton(RendererVulkan, RendererPreference.Vulkan, active);
        UpdateRendererButton(RendererOpenGl, RendererPreference.OpenGl, active);
        UpdateRendererButton(RendererCpu, RendererPreference.CpuReference, active);
    }

    private void UpdateRendererButton(Button button, RendererPreference option, RendererPreference active)
    {
        bool selected = option == active;
        button.Background = (Brush)FindResource(selected ? "Selection" : "Surface");
        button.BorderBrush = (Brush)FindResource(selected ? "Accent" : "Border");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private sealed record SceneObjectItem(Guid Id, string Name, string Location)
    {
        public string AccessibleName => $"{Name}, {Location}";
    }
}
