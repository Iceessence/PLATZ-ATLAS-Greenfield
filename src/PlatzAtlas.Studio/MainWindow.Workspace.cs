using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PlatzAtlas.Core;
using PlatzAtlas.RoomView;

namespace PlatzAtlas.Studio;

public partial class MainWindow
{
    private readonly List<ScenePreset> _presets = StarterPresets.Items.ToList();
    private RoomViewTransform _editorView = RoomViewTransform.Fit;
    private (RoomViewTransform View, double X, double Y)? _pan;
    private double _gridSpacing = .05;
    private IReadOnlyDictionary<Guid, long> _sceneNumbers = new Dictionary<Guid, long>();

    private void ExamplesClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button || _play is not null)
            return;
        MuseumExample.CommandTarget = this;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void WorkspaceSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (_play is null)
            SideTabs.SelectedItem = WorkspaceSettingsTab;
    }

    private void OutlinerMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.Items[0] is MenuItem browse)
            browse.IsEnabled = _play is null && _session.Current.Scene is not null;
    }

    private void BrowsePresetsClicked(object sender, RoutedEventArgs e)
    {
        if (_play is not null || _session.Current.Scene is not { } room)
            return;
        var browser = new PresetBrowserWindow(_presets, room) { Owner = this };
        if (browser.ShowDialog() == true && browser.Result is { } entities)
            PlacePresets(entities);
    }

    internal void PlacePresets(IReadOnlyList<SceneEntity> entities)
    {
        foreach (var entity in entities)
            ApplyScene(new AddSceneEntity(_session.Current.ProjectId, _session.Current.Revision, entity),
                $"Placed {entity.Name}. Drag it or edit its transform.");
        SideTabs.SelectedItem = InspectorTab;
    }

    private void SaveObjectPresetClicked(object sender, RoutedEventArgs e)
    {
        if (_play is not null || SelectedEntity is not { Component: not SceneSprite } entity)
            return;
        try
        {
            var preset = ScenePreset.FromObject(entity);
            if (PresetBrowserWindow.SavePreset(this, preset))
            {
                _presets.Add(preset);
                ShowStatus($"Saved {preset.Title} as a reusable preset.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowStatus(error.Message, error: true);
        }
    }

    private void GridSpacingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string interval } } &&
            double.TryParse(interval, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            _gridSpacing = value;
            if (IsLoaded && _session.Current.Scene is not null)
                UpdateScenePresentation();
        }
    }

    private void ZoomClicked(object sender, RoutedEventArgs e)
    {
        if (_play is not null || _session.Current.Scene is null || sender is not Button { Tag: string command })
            return;
        if (command == "selection")
        {
            if (SelectedEntity is not { Transform: var selected })
            {
                ShowStatus("Select an object to frame it.");
                return;
            }
            var extent = Math.Sqrt(selected.Width * selected.Width + selected.Height * selected.Height);
            _editorView = new RoomViewTransform(Math.Clamp(.64 / extent, .65, 12), selected.X, selected.Y);
        }
        else
            _editorView = command == "fit" ? RoomViewTransform.Fit :
                _editorView.Around(.5, .5, command == "in" ? 1.2 : 1 / 1.2);
        UpdateGeneralPresentation();
    }

    private bool HandleNavigation(PointerPhase phase, double x, double y)
    {
        if (phase == PointerPhase.Cancel)
            _pan = null;
        switch (phase)
        {
            case PointerPhase.ZoomIn:
            case PointerPhase.ZoomOut:
                if (_sceneDrag is null && _pan is null && x is >= 0 and <= 1 && y is >= 0 and <= 1)
                {
                    _editorView = _editorView.Around(x, y, phase == PointerPhase.ZoomIn ? 1.15 : 1 / 1.15);
                    UpdateGeneralPresentation();
                }
                return true;
            case PointerPhase.PanDown:
                _pan = (_editorView, x, y);
                return true;
            case PointerPhase.PanMove:
            case PointerPhase.PanUp:
                if (_pan is { } drag)
                {
                    _editorView = drag.View with
                    {
                        CenterX = Math.Clamp(drag.View.CenterX - (x - drag.X) / drag.View.Zoom, -.4, 1.4),
                        CenterY = Math.Clamp(drag.View.CenterY - (y - drag.Y) / drag.View.Zoom, -.4, 1.4)
                    };
                    UpdateGeneralPresentation();
                }
                if (phase == PointerPhase.PanUp)
                    _pan = null;
                return true;
            default:
                return false;
        }
    }

    private string ReadableId(Guid id, string prefix = "") =>
        _sceneNumbers.TryGetValue(id, out var number) ? $"#{prefix}{number:D6}" : "—";
}
