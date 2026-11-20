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
        PlayToggle.Visibility = Visibility.Collapsed;
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
        }
        UpdateRevision();
        UpdateGeneralPresentation(editStarted);
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateGeneralPresentation(long? editStarted = null)
    {
        var current = _session.Current;
        var entities = current.Scene!.Entities;
        if (_sceneDrag is { } drag)
            entities = entities.Select(entity => entity.Id == drag.Id ? entity with { Transform = drag.Preview } : entity).ToArray();
        AtlasPreviewBanner.Visibility = PlayBanner.Visibility = PlayPanel.Visibility =
            WestCard.Visibility = EastCard.Visibility = Visibility.Collapsed;
        SideTabs.Visibility = Visibility.Visible;
        Objects.IsEnabled = true;
        AutomationProperties.SetName(Viewport, "Room canvas");
        Viewport.Show(SceneArt.Create(current.ProjectId, current.Revision, entities, SelectedEntityId, grid: true), editStarted);
    }

    private void AddSceneClicked(object sender, RoutedEventArgs e)
    {
        if (_session.Current.Scene is not { } scene || sender is not Button { Tag: string kind })
            return;
        var lamp = kind == "Lamp";
        var offset = .035 * (scene.Entities.Count % 5);
        var entity = new SceneEntity(Guid.NewGuid(), lamp ? "Lamp" : "Wall",
            new Transform2D(.45 + offset, .46 + offset, lamp ? .1 : .36, lamp ? .16 : .105, 0),
            lamp ? new SceneLamp(true) : new WallPanel(PanelColor.Slate));
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

    private void ApplyScene(SceneOperation operation, string status)
    {
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

    private static string ComponentLabel(SceneComponent component) => component is WallPanel ? "Wall" : "Lamp";
    private sealed record SceneDrag(Guid Id, long Revision, double X, double Y, Transform2D Original, Transform2D Preview);
}
