using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PlatzAtlas.Core;
using PlatzAtlas.RoomView;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    private SceneDrag? _sceneDrag;
    private Guid? SelectedEntityId => (Objects.SelectedItem as SceneObjectItem)?.Id;
    private SceneEntity? SelectedEntity => _session.Current.Scene?.Entities.SingleOrDefault(item => item.Id == SelectedEntityId);

    private void NewBlankClicked(object sender, RoutedEventArgs e)
    {
        if (_play is null && ConfirmLeave())
            SetSession(ProjectSession.NewScene(), "Add a wall or lamp to start your room.");
    }

    private void RefreshSceneList(Guid? selectedId)
    {
        var project = _session.Current;
        var items = project.Scene is { } scene
            ? scene.Entities.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id)
                .Select(item => new SceneObjectItem(item.Id, item.Name, ComponentLabel(item.Component))).ToArray()
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
        RoomTitle.Text = project.Name;
        ObjectCount.Text = $"{project.Scene!.Entities.Count} objects";
        SelectedName.Text = entity?.Name ?? "Select an object";
        SelectedLocation.Text = entity is null ? "" : ComponentLabel(entity.Component);
        SceneInspector.Visibility = entity is null ? Visibility.Collapsed : Visibility.Visible;
        ObjectId.Text = entity?.Id.ToString() ?? "—";
        ProjectId.Text = project.ProjectId.ToString();
        RefreshAssetBrowser();
        if (entity is not null)
        {
            SceneName.Text = entity.Name;
            SetTransformFields(entity.Transform);
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
        }
        UpdateRevision();
        UpdateGeneralPresentation(editStarted);
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateGeneralPresentation(long? editStarted = null)
    {
        var current = _session.Current;
        var play = _play?.Current;
        var entities = play?.Entities ?? current.Scene!.Entities;
        if (play is null && _sceneAtlasDraft is { } draft)
            entities = entities.Select(entity => entity.Id == draft.Preview.After.Id ? draft.Preview.After : entity).ToArray();
        if (play is null && _sceneDrag is { } drag)
            entities = entities.Select(entity => entity.Id == drag.Id ? entity with { Transform = drag.Preview } : entity).ToArray();
        AtlasPreviewBanner.Visibility = WestCard.Visibility = EastCard.Visibility = Visibility.Collapsed;
        UpdatePlayControls();
        SceneAtlasPreviewBanner.Visibility = _sceneAtlasDraft is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(Viewport, play is null ? "Room canvas" : "Playable room canvas");
        Viewport.Show(SceneArt.Create(current.ProjectId, current.Revision, entities,
            play is null ? _sceneAtlasDraft?.Preview.After.Id ?? SelectedEntityId : _lastPlaySwitch?.TargetId,
            grid: play is null, play: play, nearbySwitch: _play?.NearbySwitch?.SwitchId, assets: current.Assets), editStarted);
    }

    private void AddSceneClicked(object sender, RoutedEventArgs e)
    {
        if (_play is not null || _session.Current.Scene is not { } scene || sender is not Button { Tag: string kind })
            return;
        var (component, width, height, rotation) = kind switch
        {
            "Lamp" => ((SceneComponent)new SceneLamp(true), .10, .16, 0),
            "Door" => (new SceneDoor(false), .18, .09, 0),
            "Switch" => (new SceneSwitch(null, SwitchBehavior.Toggle), .045, .077, 45),
            _ => (new WallPanel(PanelColor.Slate), .36, .105, 0)
        };
        var offset = .035 * (scene.Entities.Count % 5);
        var entity = new SceneEntity(Guid.NewGuid(), kind,
            new Transform2D(.45 + offset, .46 + offset, width, height, rotation), component);
        ApplyScene(new AddSceneEntity(_session.Current.ProjectId, _session.Current.Revision, entity),
            $"Added {entity.Name.ToLowerInvariant()}. Drag it on the canvas or edit its transform.");
    }

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
            SwitchEntityMode.SelectedValue is not string mode || !Enum.TryParse<SwitchBehavior>(mode, out var behavior))
            return;
        ApplyScene(new ConfigureSceneSwitch(_session.Current.ProjectId, _session.Current.Revision,
            entity.Id, target.Id, behavior), target.Id is null ? "Switch disconnected." : $"Switch connected to {target.Label}.");
    }

    private void UpdateSwitchEditor(SceneSwitch? control, IReadOnlyList<SceneEntity> entities)
    {
        SceneSwitchEditor.Visibility = control is null ? Visibility.Collapsed : Visibility.Visible;
        if (control is null)
            return;
        var eligible = entities.Where(item => item.Component is SceneDoor or SceneLamp).ToArray();
        var duplicates = eligible.GroupBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var targets = eligible.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id)
            .Select(item => new SwitchTargetItem(item.Id,
                duplicates.Contains(item.Name) ? $"{item.Name} · {item.Id.ToString("N")[..8]}" : item.Name))
            .Prepend(new SwitchTargetItem(null, "Unconnected")).ToArray();
        SwitchEntityTarget.ItemsSource = targets;
        SwitchEntityTarget.SelectedItem = targets.Single(target => target.Id == control.TargetId);
        SwitchEntityMode.SelectedValue = control.Behavior.ToString();
    }

    private void ApplyScene(SceneOperation operation, string status)
    {
        if (_play is not null)
            return;
        try
        {
            if (!_session.Apply(operation))
                return;
            RefreshSceneList(operation.EntityId);
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
        if (phase == PointerPhase.Down)
        {
            var hit = SceneArt.PaintOrder(scene.Entities).Reverse().FirstOrDefault(item => item.Transform.Contains(x, y));
            Objects.SelectedItem = Objects.Items.OfType<SceneObjectItem>().FirstOrDefault(item => item.Id == hit?.Id);
            _sceneDrag = hit is null ? null : new SceneDrag(hit.Id, _session.Current.Revision, x, y, hit.Transform, hit.Transform);
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
        var moved = drag.Original with
        {
            X = Math.Round(Math.Clamp(drag.Original.X + x - drag.X, 0, 1), 6),
            Y = Math.Round(Math.Clamp(drag.Original.Y + y - drag.Y, 0, 1), 6)
        };
        if (phase == PointerPhase.Up)
        {
            _sceneDrag = null;
            if (moved != drag.Original && Math.Max(Math.Abs(x - drag.X), Math.Abs(y - drag.Y)) >= .002)
                ApplyScene(new TransformSceneEntity(_session.Current.ProjectId, drag.Revision, drag.Id, moved), "Moved the object.");
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
        _ => "Lamp"
    };
    private sealed record SwitchTargetItem(Guid? Id, string Label);
    private sealed record SceneDrag(Guid Id, long Revision, double X, double Y, Transform2D Original, Transform2D Preview);
}
