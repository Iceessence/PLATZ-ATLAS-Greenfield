using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    public static readonly RoutedUICommand TogglePlayCommand = new("Toggle Play", nameof(TogglePlayCommand), typeof(MainWindow));

    private MuseumPlaySession? _play;
    private PlaySwitchResult? _lastPlaySwitch;

    private bool CanStartPlay => !_session.IsDirty && _session.FilePath is not null;

    private void CanTogglePlay(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _play is not null || CanStartPlay;

    private void TogglePlayExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_play is not null)
        {
            StopPlay("stop");
            UpdateStudio();
            PlayToggle.Focus();
            ShowStatus($"Stopped Play. Returned to saved revision {_session.Current.Revision}.");
            return;
        }
        if (!CanStartPlay)
            return;

        var started = Stopwatch.GetTimestamp();
        try
        {
            var session = new MuseumPlaySession(_session.ReadSavedSnapshot());
            ClearAtlasAccess();
            _play = session;
            var snapshot = session.Current;
            PlaySwitches.ItemsSource = session.Switches.Select(item =>
                new PlaySwitchItem(snapshot.SessionId, item.Id, $"Use {item.Name}")).ToArray();
            UpdateStudio();
            PlayToggle.Focus();
            ShowStatus($"Playing saved revision {snapshot.SourceRevision}. Stop to return to editing.");
            _diagnostics.Log("play-started", new
            {
                snapshot.SessionId, snapshot.ProjectId, snapshot.SceneId, snapshot.SourceRevision,
                durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowStatus($"Play could not start. {error.Message}", error: true);
            _diagnostics.Log("play-rejected", new { reason = error is ProjectEditException edit ? edit.Reason.ToString() : "SourceUnavailable" });
        }
    }

    private void StopPlay(string reason)
    {
        if (_play is not { } session)
            return;

        var snapshot = session.Current;
        session.Stop();
        _play = null;
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

        _lastPlaySwitch = result;
        UpdateScenePresentation();
        UIElementAutomationPeer.FromElement(PlayResultText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _diagnostics.Log("play-switch", new
        {
            session.Current.SessionId, session.Current.SourceRevision,
            result.Step, result.SwitchId, result.TargetId, result.WasOn, result.IsOn,
            durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });
    }

    private void UpdatePlayControls()
    {
        var snapshot = _play?.Current;
        var playing = snapshot is not null;
        PlayToggle.Content = playing ? "Stop" : "Play";
        PlayBanner.Visibility = PlayPanel.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        SideTabs.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        Objects.IsEnabled = !playing;
        PlayBannerText.Text = snapshot is null ? "" :
            $"Playing saved r{snapshot.SourceRevision} · {snapshot.Step} switch {(snapshot.Step == 1 ? "action" : "actions")}";
        PlayEmpty.Visibility = _play?.Switches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayResultCard.Visibility = _lastPlaySwitch is not null ? Visibility.Visible : Visibility.Collapsed;
        PlayResultTitle.Text = _lastPlaySwitch?.SwitchName ?? "";
        PlayResultText.Text = _lastPlaySwitch is { } change ?
            $"{change.TargetLocation} light · {LightState(change.WasOn)} → {LightState(change.IsOn)}" : "";
    }

    private sealed record PlaySwitchItem(Guid SessionId, Guid SwitchId, string Label);
}
