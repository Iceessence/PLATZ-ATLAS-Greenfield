using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.RoomView;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    public static readonly RoutedUICommand DuplicateSceneCommand = new("Duplicate object", nameof(DuplicateSceneCommand), typeof(MainWindow));
    private SceneDrag? _sceneDrag;
    private Guid? SelectedEntityId => (Objects.SelectedItem as SceneObjectItem)?.Id;
    private SceneEntity? SelectedEntity => _session.Current.Scene?.Entities.SingleOrDefault(item => item.Id == SelectedEntityId);

    private void NewBlankClicked(object sender, RoutedEventArgs e)
    {
        if (_play is null && ConfirmLeave())
            SetSession(ProjectSession.NewScene(), "Browse presets to place your first object.");
    }

    private void RefreshSceneList(Guid? selectedId)
    {
        var project = _session.Current;
        _sceneNumbers = project.Scene is { } numbered ? SceneNumbers.FromHistory(numbered) : new Dictionary<Guid, long>();
        var items = project.Scene is { } scene
            ? scene.Entities.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id)
                .Select(item => new SceneObjectItem(item.Id, item.Name, ComponentLabel(item.Component), ReadableId(item.Id),
                    PresetBrowserWindow.ComponentIcon(item.Component))).ToArray()
            : project.Objects.OrderBy(item => item.X)
                .Select(item => new SceneObjectItem(item.Id, item.Name, item.Location)).ToArray();
        var wasChanging = _changingProject;
        _changingProject = true;
        Objects.ItemsSource = items;
        Objects.SelectedItem = items.FirstOrDefault(item => item.Id == selectedId) ?? items.FirstOrDefault();
        _changingProject = wasChanging;
    }

    private void UpdateGeneralEditor(long? editStarted)
    {
        var project = _session.Current;
        var entity = SelectedEntity;
        RoomTitle.Text = project.DisplayName;
        RoomNameEdit.Text = project.DisplayName;
        FloorColorEdit.Text = $"{project.Scene!.Appearance?.BackgroundRgb ?? SceneAppearance.DefaultBackgroundRgb:X6}";
        var count = project.Scene!.Entities.Count;
        ObjectCount.Text = count == 1 ? "1 object" : $"{count} objects";
        SelectedName.Text = entity?.Name ?? "Select an object";
        SelectedLocation.Text = entity is null ? "" : $"{ComponentLabel(entity.Component)} · {ReadableId(entity.Id, "C")}";
        SceneInspector.Visibility = entity is null ? Visibility.Collapsed : Visibility.Visible;
        ObjectId.Text = entity is null ? "—" : ReadableId(entity.Id);
        ObjectId.ToolTip = entity?.Id.ToString();
        ProjectId.Text = project.ProjectId.ToString();
        RefreshAssetBrowser();
        if (entity is not null)
        {
            SceneName.Text = entity.Name;
            SetTransformFields(entity.Transform);
            var order = SceneAppearance.Ordered(project.Scene.Entities, project.Scene.Appearance?.PaintOrder).ToArray();
            var layer = Array.FindIndex(order, item => item.Id == entity.Id);
            BringForward.IsEnabled = layer < order.Length - 1;
            SendBackward.IsEnabled = layer > 0;
            ChangeColor.Visibility = entity.Component is WallPanel ? Visibility.Visible : Visibility.Collapsed;
            if (entity.Component is WallPanel wall)
                ChangeColor.Content = $"Wall color · {wall.Color}";
            ChangeLight.Visibility = entity.Component is SceneLamp ? Visibility.Visible : Visibility.Collapsed;
            if (entity.Component is SceneLamp lamp)
                ChangeLight.Content = lamp.IsOn ? "Turn lamp off" : "Turn lamp on";
            ChangeDoor.Visibility = entity.Component is SceneDoor ? Visibility.Visible : Visibility.Collapsed;
            if (entity.Component is SceneDoor door)
                ChangeDoor.Content = door.IsOpen ? "Set initially closed" : "Set initially open";
            UpdateSwitchEditor(entity.Component as SceneSwitch, project.Scene.Entities);
            GoalEditor.Visibility = entity.Component is SceneGoal ? Visibility.Visible : Visibility.Collapsed;
            SaveObjectPreset.Visibility = entity.Component is SceneSprite ? Visibility.Collapsed : Visibility.Visible;
            if (entity.Component is SceneGoal goal)
                RequiredKeys.Text = goal.RequiredKeys.ToString(CultureInfo.InvariantCulture);
        }
        UpdateRevision();
        UpdateGeneralPresentation(editStarted);
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateGeneralPresentation(long? editStarted = null, bool drawOnly = false)
    {
        var current = _session.Current;
        var play = _play?.Current;
        var entities = play?.Entities ?? current.Scene!.Entities;
        if (play is null && _sceneAtlasDraft is { } draft)
            entities = draft.Plan is { } plan ? plan.Entities : draft.Preview is { } preview
                ? preview.Before is null ? entities.Append(preview.After).ToArray() :
                    entities.Select(entity => entity.Id == preview.After.Id ? preview.After : entity).ToArray()
                : entities;
        if (play is null && _sceneDrag is { } drag)
            entities = entities.Select(entity => entity.Id == drag.Id ? entity with { Transform = drag.Preview } : entity).ToArray();
        if (!drawOnly)
        {
            var animated = current.Assets.Where(asset => asset.MediaType == PixelClip.MediaType).Select(asset => asset.Id).ToHashSet();
            _animation.SetActive(entities.Any(entity => entity.Component is SceneSprite sprite && animated.Contains(sprite.AssetId)));
            AtlasPreviewBanner.Visibility = WestCard.Visibility = EastCard.Visibility = Visibility.Collapsed;
            UpdatePlayControls();
            SceneAtlasPreviewBanner.Visibility = _sceneAtlasDraft is null ? Visibility.Collapsed : Visibility.Visible;
            if (play is null)
                ViewportCoordinates.Text = $"{_editorView.Zoom:P0}  ·  Wheel to zoom  ·  Middle drag to pan";
            AutomationProperties.SetName(Viewport, play is null ? "Room canvas" : "Playable room canvas");
        }
        Viewport.Show(SceneArt.Create(current.ProjectId, current.Revision, entities,
            play is null ? _sceneAtlasDraft?.Preview?.After.Id ?? SelectedEntityId : _lastPlaySwitch?.TargetId,
            grid: play is null, play: play, nearbySwitch: _play?.NearbySwitch?.SwitchId, assets: current.Assets,
            backgroundRgb: current.Scene!.Appearance?.BackgroundRgb, paintOrder: current.Scene.Appearance?.PaintOrder,
            editHandles: play is null && _sceneAtlasDraft is null,
            view: play is null ? _editorView : null, gridSpacing: _gridSpacing,
            animationMs: _animation.Milliseconds), editStarted);
    }

    private void UpdateGeneralPresentationFromClock() => UpdateGeneralPresentation(drawOnly: true);

    private void RenameSceneClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedEntity is { } entity)
            ApplyScene(new RenameSceneEntity(_session.Current.ProjectId, _session.Current.Revision, entity.Id, SceneName.Text),
                "Renamed the selected object.");
    }

    private void ApplyTransformClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedEntity is not { } entity)
            return;
        if (!Read(SceneX, out var x) || !Read(SceneY, out var y) || !Read(SceneWidth, out var width) ||
            !Read(SceneHeight, out var height) || !Read(SceneRotation, out var rotation))
        {
            ShowStatus("Enter numbers for X, Y, width, height and rotation.", error: true);
            return;
        }
        ApplyScene(new TransformSceneEntity(_session.Current.ProjectId, _session.Current.Revision, entity.Id,
            new Transform2D(x, y, width, height, rotation)), "Updated the object transform.");

        static bool Read(TextBox field, out double value) =>
            double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
            double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void ChangeSceneColorClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedEntity is { Component: WallPanel wall } entity)
        {
            var next = (PanelColor)(((int)wall.Color + 1) % Enum.GetValues<PanelColor>().Length);
            ApplyScene(new SetScenePanelColor(_session.Current.ProjectId, _session.Current.Revision, entity.Id, next),
                $"Set wall color to {next}.");
        }
    }

    private void ChangeSceneLampClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedEntity is { Component: SceneLamp lamp } entity)
            ApplyScene(new SetSceneLamp(_session.Current.ProjectId, _session.Current.Revision, entity.Id, !lamp.IsOn),
                lamp.IsOn ? "Turned the lamp off." : "Turned the lamp on.");
    }

    private void DeleteSceneClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedEntity is { } entity)
            ApplyScene(new DeleteSceneEntity(_session.Current.ProjectId, _session.Current.Revision, entity.Id),
                $"Deleted {entity.Name}. Use Undo to restore it.");
    }

    private void ChangeSceneDoorClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedEntity is { Component: SceneDoor door } entity)
            ApplyScene(new SetSceneDoor(_session.Current.ProjectId, _session.Current.Revision, entity.Id, !door.IsOpen),
                door.IsOpen ? "Door starts closed." : "Door starts open.");
    }

    private void ConfigureSceneSwitchClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedEntity is not { Component: SceneSwitch } entity ||
            SwitchEntityTarget.SelectedItem is not SwitchTargetItem target ||
            SwitchSecondTarget.SelectedItem is not SwitchTargetItem second ||
            SwitchEntityMode.SelectedValue is not string mode || !Enum.TryParse<SwitchBehavior>(mode, out var behavior))
            return;
        ApplyScene(new ConfigureSceneSwitch(_session.Current.ProjectId, _session.Current.Revision,
            entity.Id, target.Id, behavior) { SecondTargetId = target.Id is null ? null : second.Id },
            target.Id is null ? "Switch disconnected." : second.Id is null
                ? $"Switch connected to {target.Label}." : $"Switch connected to {target.Label} and {second.Label}.");
    }

    private void UpdateSwitchEditor(SceneSwitch? control, IReadOnlyList<SceneEntity> entities)
    {
        SceneSwitchEditor.Visibility = control is null ? Visibility.Collapsed : Visibility.Visible;
        if (control is null)
            return;
        var eligible = entities.Where(item => item.Component is SceneDoor or SceneLamp).ToArray();
        var targets = eligible.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id)
            .Select(item => new SwitchTargetItem(item.Id, $"{ReadableId(item.Id)} · {item.Name}"))
            .Prepend(new SwitchTargetItem(null, "Unconnected")).ToArray();
        SwitchEntityTarget.ItemsSource = targets;
        SwitchEntityTarget.SelectedItem = targets.Single(target => target.Id == control.TargetId);
        SwitchSecondTarget.ItemsSource = targets;
        SwitchSecondTarget.SelectedItem = targets.Single(target => target.Id == control.SecondTargetId);
        SwitchEntityMode.SelectedValue = control.Behavior.ToString();
    }

    private void ApplyGoalClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedEntity is not { Component: SceneGoal } entity)
            return;
        if (!int.TryParse(RequiredKeys.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 0 or > 64)
        {
            ShowStatus("Enter a key requirement from 0 to 64.", error: true);
            return;
        }
        ApplyScene(new SetSceneGoal(_session.Current.ProjectId, _session.Current.Revision, entity.Id, count),
            $"The goal requires {count} {(count == 1 ? "key" : "keys")}.");
    }

    private void ApplyScene(SceneOperation operation, string status)
    {
        if (_play is not null)
            return;
        try
        {
            if (!_session.Apply(operation))
                return;
            RefreshSceneList(operation is ConfigureRoomAppearance ? SelectedEntityId : operation.EntityId);
            UpdateStudio();
            ShowStatus(status);
            _diagnostics.Log("scene-edit", new { operation = operation.GetType().Name, operation.EntityId, _session.Current.Revision });
        }
        catch (Exception error) when (error is ProjectEditException or InvalidDataException)
        {
            ShowStatus(error.Message, error: true);
        }
    }

    private void ScenePointerInput(PointerPhase phase, double x, double y)
    {
        if (_play is not null)
            return;
        if (_session.Current.Scene is not { } scene)
        {
            if (phase == PointerPhase.Down)
                SelectAt(x, y);
            return;
        }
        if (HandleNavigation(phase, x, y))
            return;
        (x, y) = _editorView.ToWorld(x, y);
        if (phase == PointerPhase.Move)
            ViewportCoordinates.Text = $"{_editorView.Zoom:P0}  ·  X {x:0.000}  Y {y:0.000}";
        if (phase == PointerPhase.Down)
        {
            var mode = DragMode.Move;
            var hit = SelectedEntity;
            if (hit is not null && _sceneAtlasDraft is null)
            {
                var handles = SceneArt.EditHandles(hit.Transform);
                if (Near(handles.Rotate, x, y)) mode = DragMode.Rotate;
                else if (Near(handles.Resize, x, y)) mode = DragMode.Resize;
            }
            if (mode == DragMode.Move)
                hit = SceneAppearance.Ordered(scene.Entities, scene.Appearance?.PaintOrder).Reverse()
                    .FirstOrDefault(item => item.Transform.Contains(x, y));
            Objects.SelectedItem = Objects.Items.OfType<SceneObjectItem>().FirstOrDefault(item => item.Id == hit?.Id);
            _sceneDrag = hit is null ? null : new SceneDrag(hit.Id, _session.Current.Revision, x, y, hit.Transform, hit.Transform, mode);
            return;
        }
        if (_sceneDrag is not { } drag)
            return;
        if (phase == PointerPhase.Cancel || drag.Revision != _session.Current.Revision)
        {
            _sceneDrag = null;
            UpdateGeneralEditor(null);
            return;
        }
        var moved = DragTransform(drag, x, y);
        if (phase == PointerPhase.Up)
        {
            _sceneDrag = null;
            if (moved != drag.Original && Math.Max(Math.Abs(x - drag.X), Math.Abs(y - drag.Y)) >= .002)
                ApplyScene(new TransformSceneEntity(_session.Current.ProjectId, drag.Revision, drag.Id, moved),
                    drag.Mode switch { DragMode.Rotate => "Rotated the object.", DragMode.Resize => "Resized the object.", _ => "Moved the object." });
            else
                UpdateGeneralEditor(null);
            return;
        }
        if (moved != drag.Preview)
        {
            _sceneDrag = drag with { Preview = moved };
            SetTransformFields(moved);
            UpdateGeneralPresentation();
        }
    }

    private Transform2D DragTransform(SceneDrag drag, double x, double y)
    {
        var t = drag.Original;
        var dx = x - drag.X;
        var dy = y - drag.Y;
        var angle = t.Rotation * Math.PI / 180;
        return drag.Mode switch
        {
            DragMode.Resize => t with
            {
                Width = Bound(t.Width + 2 * (dx * Math.Cos(angle) + dy * Math.Sin(angle)), .025, 1),
                Height = Bound(t.Height + 2 * (dy * Math.Cos(angle) - dx * Math.Sin(angle)), .025, 1)
            },
            DragMode.Rotate => t with
            {
                Rotation = Bound(Math.IEEERemainder(t.Rotation +
                    (Math.Atan2(y - t.Y, x - t.X) - Math.Atan2(drag.Y - t.Y, drag.X - t.X)) * (180 / Math.PI), 360), -180, 180, 15)
            },
            _ => t with { X = Bound(t.X + dx, 0, 1), Y = Bound(t.Y + dy, 0, 1) }
        };

        double Bound(double value, double minimum, double maximum, double? interval = null) =>
            Math.Round(Math.Clamp(SnapToGrid.IsChecked == true
                ? Math.Round(value / (interval ?? _gridSpacing), MidpointRounding.AwayFromZero) * (interval ?? _gridSpacing)
                : value, minimum, maximum), 6);
    }

    private static bool Near(RenderPoint point, double x, double y) =>
        Math.Abs(point.X - x) <= .015 && Math.Abs(point.Y - y) <= .025;

    private void CanDuplicateScene(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _play is null && SelectedEntity is { Component: not SceneSpawn };

    private void DuplicateSceneExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (SelectedEntity is { } entity)
            ApplyScene(new DuplicateSceneEntity(_session.Current.ProjectId, _session.Current.Revision, Guid.NewGuid(), entity.Id),
                $"Duplicated {entity.Name}.");
    }

    private void ShiftLayerClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedEntity is { } entity && sender is Button { Tag: string direction })
            ApplyScene(new ShiftSceneLayer(_session.Current.ProjectId, _session.Current.Revision, entity.Id, direction == "forward"),
                direction == "forward" ? "Brought the object forward." : "Sent the object backward.");
    }

    private void ApplyRoomAppearanceClicked(object sender, RoutedEventArgs e)
    {
        var hex = FloorColorEdit.Text.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            ShowStatus("Enter a six-digit RGB floor color.", error: true);
            return;
        }
        ApplyScene(new ConfigureRoomAppearance(_session.Current.ProjectId, _session.Current.Revision,
            _session.Current.SceneId, RoomNameEdit.Text, rgb), "Updated the room name and floor color.");
    }

    private static string SafeProjectFileName(string name) =>
        new string(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray()) + ".platz";

    private void SetTransformFields(Transform2D t)
    {
        SceneX.Text = t.X.ToString("G", CultureInfo.CurrentCulture);
        SceneY.Text = t.Y.ToString("G", CultureInfo.CurrentCulture);
        SceneWidth.Text = t.Width.ToString("G", CultureInfo.CurrentCulture);
        SceneHeight.Text = t.Height.ToString("G", CultureInfo.CurrentCulture);
        SceneRotation.Text = t.Rotation.ToString("G", CultureInfo.CurrentCulture);
    }

    private static string ComponentLabel(SceneComponent component) => component switch
    {
        WallPanel => "Wall",
        SceneDoor => "Door",
        SceneSwitch => "Switch",
        SceneSprite => "Image",
        SceneSpawn => "Player spawn",
        SceneKey => "Collectible key",
        SceneGoal => "Goal",
        _ => "Lamp"
    };
    private sealed record SwitchTargetItem(Guid? Id, string Label);
    private enum DragMode { Move, Resize, Rotate }
    private sealed record SceneDrag(Guid Id, long Revision, double X, double Y, Transform2D Original, Transform2D Preview, DragMode Mode);
}
