using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.RoomView;
using PlatzAtlas.Runtime;

namespace PlatzAtlas.Player;

public partial class PlayerWindow : Window
{
    private readonly string? _diagnostics;
    private RuntimeRoom? _room;
    private MuseumPlaySession? _session;
    private PlaySwitchResult? _lastSwitch;

    internal PlayerWindow(RendererPreference renderer, string? roomPath, string? diagnostics)
    {
        if (diagnostics is not null)
        {
            _diagnostics = Path.GetFullPath(diagnostics);
            using var file = new FileStream(_diagnostics, FileMode.CreateNew, FileAccess.Write);
        }
        InitializeComponent();
        Viewport.SetPreference(renderer);
        Viewport.VulkanValidated += summary => Log("vulkan-validation", summary);
        Viewport.FrameCompleted += frame =>
        {
            BackendLabel.Text = frame.Label;
            BackendLabel.ToolTip = frame.Detail;
            Log("frame", frame);
        };
        StateChanged += (_, _) => Viewport.Refresh();
        if (roomPath is not null)
            OpenRoom(roomPath);
    }

    protected override void OnClosed(EventArgs e)
    {
        _session?.Stop();
        Viewport.Shutdown();
        base.OnClosed(e);
    }

    private void OpenClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PLATZ Room (*.platzroom)|*.platzroom" };
        if (dialog.ShowDialog(this) == true)
            OpenRoom(dialog.FileName);
    }

    private void OpenRoom(string path)
    {
        try
        {
            var room = RoomPackage.Open(path);
            StartRoom(room);
            Log("room-opened", new { room.ProjectId, room.SceneId, room.SourceRevision, profile = room.Profile });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Room could not open. {error.Message}", error: true);
            Log("room-rejected", new { error = error.Message });
        }
    }

    private void StartRoom(RuntimeRoom room)
    {
        var session = new MuseumPlaySession(room);
        _session?.Stop();
        _session = session;
        _room = room;
        _lastSwitch = null;
        RoomTitle.Text = room.Name;
        SourceLabel.Text = $"Exported revision {room.SourceRevision}";
        RestartButton.IsEnabled = Controls.IsEnabled = true;
        SetStatus(session.Markers.Count == 0 ? "Explore the room with the arrow keys or WASD." :
            "Walk toward a switch and use it when its marker turns gold.");
        UpdateRoom();
    }

    private void RestartClicked(object sender, RoutedEventArgs e)
    {
        if (_room is { } room)
            StartRoom(room);
    }

    private void MoveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<WalkDirection>(tag, out var direction))
            Walk(direction);
    }

    private void PlayerKeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null || Keyboard.Modifiers != ModifierKeys.None)
            return;
        var direction = WalkInput.Direction(e.Key);
        if (direction is { } movement)
            Walk(movement);
        else if (e.Key == Key.E && !e.IsRepeat)
            UseSwitch();
        else
            return;
        e.Handled = true;
    }

    private void Walk(WalkDirection direction)
    {
        if (_session is not { } session)
            return;
        var moved = session.TryWalk(session.Current.SessionId, direction);
        if (session.Current.Entities is not null)
        {
            SetStatus(session.Current.Progress?.CompletedGoal is not null ? "Goal complete. Restart room to play again." :
                moved ? "Walk or press E near a connected switch." : "Path blocked.");
            Log("walk", new { direction = direction.ToString(), moved, session.Current.Visitor, session.Current.Progress });
        }
        if (moved)
            UpdateRoom();
    }

    private void UseClicked(object sender, RoutedEventArgs e) => UseSwitch();

    private void UseSwitch()
    {
        if (_session is not { } session)
            return;
        if (!session.TryUseNearbySwitch(session.Current.SessionId, out var result))
        {
            SetStatus("Walk closer to a switch first.");
            return;
        }
        _lastSwitch = result;
        SetStatus($"{result.SwitchName} activated.");
        UpdateRoom();
        UIElementAutomationPeer.FromElement(ActionLabel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        Log("switch-used", result);
    }

    private void UpdateRoom()
    {
        var session = _session!;
        var nearby = session.NearbySwitch;
        NearbyLabel.Text = session.Current.Progress?.CompletedGoal is not null ? "Restart to play again." :
            nearby is not null ? $"In range · {nearby.Name}" :
            session.Markers.Count == 0 ? "This room has no connected switches." : "Walk toward a switch marker.";
        UseButton.IsEnabled = nearby is not null;
        ActionLabel.Text = _lastSwitch?.Summary ?? "";
        var snapshot = session.Current;
        ProgressLabel.Visibility = snapshot.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        ProgressLabel.Text = snapshot.Progress?.Summary ?? "";
        if (snapshot.Entities is { } entities)
        {
            LightLabel.Text = string.Join("\n", entities.OrderBy(item => item.Name)
                .Select(item => item.Component switch
                {
                    SceneDoor door => $"{item.Name} · {(door.IsOpen ? "Open" : "Closed")}",
                    SceneLamp lamp => $"{item.Name} · {State(lamp.IsOn)}",
                    _ => null
                }).OfType<string>());
            Viewport.Show(SceneArt.Create(snapshot.ProjectId, snapshot.SourceRevision, entities,
                _lastSwitch?.TargetId, play: snapshot, nearbySwitch: nearby?.SwitchId, assets: _room?.RasterAssets));
        }
        else
        {
            LightLabel.Text = string.Join("\n", snapshot.Objects.OrderBy(item => item.X)
                .Select(item => $"{item.Location} · {State(item.IsOn)}"));
            Viewport.Show(MuseumArt.CreatePlayer(session, _lastSwitch?.TargetId));
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusLabel.Text = message;
        StatusLabel.Foreground = (Brush)FindResource(error ? "Error" : "Muted");
        UIElementAutomationPeer.FromElement(StatusLabel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void Log(string name, object data)
    {
        if (_diagnostics is null)
            return;
        try { File.AppendAllText(_diagnostics, JsonSerializer.Serialize(new { @event = name, data }) + Environment.NewLine); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string State(bool isOn) => isOn ? "On" : "Off";
}
