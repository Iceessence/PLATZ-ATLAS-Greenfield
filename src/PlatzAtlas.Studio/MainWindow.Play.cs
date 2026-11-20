using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;
using PlatzAtlas.RoomView;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    public static readonly RoutedUICommand TogglePlayCommand = new("Toggle Play", nameof(TogglePlayCommand), typeof(MainWindow));

    private MuseumPlaySession? _play;
    private WorkingDocumentSnapshot? _playSource;
    private PlaySwitchResult? _lastPlaySwitch;

    private bool HasSavedRevision => !_session.IsDirty && _session.FilePath is not null;

    private void CanTogglePlay(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !_changingProject;

    private void TogglePlayExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_play is not null)
        {
            StopPlay("stop");
            UpdateStudio();
            PlayToggle.Focus();
            ShowStatus($"Stopped Play. Returned to working revision {_session.Current.Revision}.");
            return;
        }
        StartPlay(_session.FreezeWorkingSnapshot());
    }

    private void StartPlay(WorkingDocumentSnapshot source)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var session = new MuseumPlaySession(source.Project);
            ClearAtlasAccess();
            ResetSceneAtlas();
            _sceneDrag = null;
            _playSource = source;
            _play = session;
            _animation.Restart();
            var snapshot = session.Current;
            PlaySwitches.ItemsSource = session.Switches.Select(item =>
                new PlaySwitchItem(snapshot.SessionId, item.Id, $"Use {item.Name}")).ToArray();
            UpdateStudio();
            PlayToggle.Focus();
            ShowStatus($"Playing a copy of working revision {snapshot.SourceRevision}. F5 returns to editing.");
            _diagnostics.Log("play-started", new
            {
                snapshot.SessionId, snapshot.ProjectId, snapshot.SceneId, snapshot.SourceRevision, source.WorkspaceId,
                durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowStatus($"Play could not start. {error.Message}", error: true);
            _diagnostics.Log("play-rejected", new { reason = error is ProjectEditException edit ? edit.Reason.ToString() : "SourceUnavailable" });
        }
    }

    private void RestartPlayClicked(object sender, RoutedEventArgs e)
    {
        if (_playSource is not { } source)
            return;
        StopPlay("restart");
        StartPlay(source);
    }

    private void StopPlay(string reason)
    {
        if (_play is not { } session)
            return;

        var snapshot = session.Current;
        session.Stop();
        _play = null;
        _playSource = null;
        _lastPlaySwitch = null;
        PlaySwitches.ItemsSource = null;
        _diagnostics.Log("play-stopped", new { snapshot.SessionId, snapshot.SourceRevision, snapshot.Step, reason });
    }

    private void PlaySwitchClicked(object sender, RoutedEventArgs e)
    {
        if (_play is not { } session || sender is not Button { Tag: PlaySwitchItem input })
            return;

        var started = Stopwatch.GetTimestamp();
        if (!session.TryUseSwitch(input.SessionId, input.SwitchId, out var result))
            return;

        RecordSwitchUse(session, result, started);
    }

    private void RecordSwitchUse(MuseumPlaySession session, PlaySwitchResult result, long started)
    {
        _lastPlaySwitch = result;
        UpdateScenePresentation();
        UIElementAutomationPeer.FromElement(PlayResultText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _diagnostics.Log("play-switch", new
        {
            session.Current.SessionId, session.Current.SourceRevision,
            result.Step, result.SwitchId, result.TargetId, result.WasOn, result.IsOn, result.IsDoor,
            durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });
    }

    private void UpdatePlayControls()
    {
        var snapshot = _play?.Current;
        var playing = snapshot is not null;
        var general = snapshot?.Entities is not null;
        PlayToggle.Content = playing ? "Stop" : "Play";
        PlayBanner.Visibility = PlayPanel.Visibility = RestartPlay.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        SideTabs.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        AssistantTabs.Visibility = AssistantSplitter.Visibility = SideTabs.Visibility;
        ViewportTools.Visibility = !playing && _session.Current.Scene is not null ? Visibility.Visible : Visibility.Collapsed;
        Objects.IsEnabled = !playing;
        SceneActions.IsEnabled = NewBlank.IsEnabled = SnapToGrid.IsEnabled = !playing;
        PlayBannerText.Text = snapshot is null ? "" : snapshot.Progress is { } progress
            ? progress.TotalKeys == 0 && progress.GoalRequirement is null
                ? $"Playtest · Working r{snapshot.SourceRevision}"
                : $"Playtest · Working r{snapshot.SourceRevision} · {progress.Summary}"
            : $"Playtest · Working r{snapshot.SourceRevision} · {snapshot.Step} switch {(snapshot.Step == 1 ? "action" : "actions")}";
        PlayEmpty.Visibility = !general && _play?.Switches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScenePlayControls.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        PlaySwitches.Visibility = general ? Visibility.Collapsed : Visibility.Visible;
        if (general)
        {
            var nearby = _play!.NearbySwitch;
            SceneNearby.Text = snapshot?.Progress?.CompletedGoal is not null ? "Stop to return to editing." :
                nearby is not null ? $"In range · {nearby.Name}" : _play.Markers.Count == 0
                ? "This room has no connected switches." : "Walk toward a switch marker.";
            SceneUseSwitch.IsEnabled = nearby is not null;
        }
        PlayResultCard.Visibility = _lastPlaySwitch is not null ? Visibility.Visible : Visibility.Collapsed;
        PlayResultTitle.Text = _lastPlaySwitch?.SwitchName ?? "";
        PlayResultText.Text = _lastPlaySwitch is { } change ? general ? change.Summary :
            $"{change.TargetLocation} light · {change.StateText}" : "";
    }

    private void StudioPlayKeyDown(object sender, KeyEventArgs e)
    {
        if (_play?.Current.Entities is null || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (WalkInput.Direction(e.Key) is { } direction)
            WalkScene(direction);
        else if (e.Key == Key.E && !e.IsRepeat)
            UseSceneSwitch();
        else
            return;
        e.Handled = true;
    }

    private void SceneWalkClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<WalkDirection>(tag, out var direction))
            WalkScene(direction);
    }

    private void WalkScene(WalkDirection direction)
    {
        if (_play is not { } session || session.Current.Entities is null)
            return;
        var moved = session.TryWalk(session.Current.SessionId, direction);
        if (moved)
            UpdateScenePresentation();
        ShowStatus(session.Current.Progress?.CompletedGoal is not null ? "Goal complete. Stop to return to editing." :
            moved ? "Walking in Play. Press E near a connected switch." : "Path blocked.");
        _diagnostics.Log("play-walk", new { direction = direction.ToString(), moved, session.Current.Visitor, session.Current.Progress });
    }

    private void ScenePlayUseClicked(object sender, RoutedEventArgs e) => UseSceneSwitch();

    private void UseSceneSwitch()
    {
        if (_play is not { } session || session.Current.Entities is null)
            return;
        var started = Stopwatch.GetTimestamp();
        if (!session.TryUseNearbySwitch(session.Current.SessionId, out var result))
        {
            ShowStatus("Walk closer to a connected switch first.");
            return;
        }
        RecordSwitchUse(session, result, started);
        ShowStatus($"{result.SwitchName} activated.");
    }

    private sealed record PlaySwitchItem(Guid SessionId, Guid SwitchId, string Label);
}
