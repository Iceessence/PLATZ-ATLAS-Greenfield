using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PlatzAtlas.Core;

namespace PlatzAtlas.Studio;

public partial class PresetBrowserWindow : Window
{
    private readonly List<ScenePreset> _presets;
    private readonly SceneDocument _room;

    internal PresetBrowserWindow(List<ScenePreset> presets, SceneDocument room)
    {
        _presets = presets;
        _room = room;
        InitializeComponent();
        Filter();
    }

    internal IReadOnlyList<SceneEntity>? Result { get; private set; }

    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (PresetList is not null)
            Filter();
    }

    private void Filter(Guid? selectedId = null)
    {
        var search = PresetSearch.Text.Trim();
        PresetList.ItemsSource = _presets.Where(preset => $"{preset.Title} {preset.Category} {preset.Description}"
            .Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .Select(preset => new PresetCard(preset, ComponentIcon(preset.Template.Component))).ToArray();
        PresetList.SelectedItem = PresetList.Items.OfType<PresetCard>().FirstOrDefault(item => item.Preset.Id == selectedId)
            ?? PresetList.Items.OfType<PresetCard>().FirstOrDefault();
    }

    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        var card = PresetList.SelectedItem as PresetCard;
        PresetEditor.IsEnabled = PlacePreset.IsEnabled = SaveVariant.IsEnabled = card is not null;
        PresetStatus.Text = card is null ? "No presets match this search." : "";
        if (card is null)
        {
            PresetIcon.Data = null;
            PresetTitle.Text = PresetDescription.Text = "";
            return;
        }
        var (preset, icon) = card;
        var entity = preset.Template;
        PresetTitle.Text = preset.Title;
        PresetDescription.Text = preset.Description;
        PresetIcon.Data = icon;
        PresetName.Text = entity.Name;
        PresetWidth.Text = entity.Transform.Width.ToString("G", CultureInfo.CurrentCulture);
        PresetHeight.Text = entity.Transform.Height.ToString("G", CultureInfo.CurrentCulture);
        PresetCount.Text = "1";
        GoalRow.Visibility = entity.Component is SceneGoal ? Visibility.Visible : Visibility.Collapsed;
        PresetKeys.Text = ((entity.Component as SceneGoal)?.RequiredKeys ?? 0).ToString(CultureInfo.InvariantCulture);
        var (label, items, index) = entity.Component switch
        {
            WallPanel wall => ("Wall color", Enum.GetNames<PanelColor>(), (int)wall.Color),
            SceneDoor door => ("Initial state", new[] { "Closed", "Open" }, door.IsOpen ? 1 : 0),
            SceneLamp lamp => ("Initial state", new[] { "Off", "On" }, lamp.IsOn ? 1 : 0),
            SceneSwitch control => ("Activation behavior", new[] { "Toggle", "Turn on / Open" }, (int)control.Behavior),
            _ => ("", Array.Empty<string>(), -1)
        };
        ChoiceRow.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ChoiceLabel.Text = label;
        PresetChoice.ItemsSource = items;
        PresetChoice.SelectedIndex = index;
    }

    internal SceneEntity[] ConfigurePlacement()
    {
        if (PresetList.SelectedItem is not PresetCard { Preset: { } preset })
            throw new InvalidDataException("Choose a preset.");
        if (!int.TryParse(PresetCount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 12)
            throw new InvalidDataException("Place between 1 and 12 instances at a time.");
        if (count + _room.Entities.Count > SceneValidation.MaxEntities)
            throw new InvalidDataException("There is no room for this many objects.");
        var template = ReadVariant(preset);
        if (template.Component is SceneSpawn && (count != 1 || _room.Entities.Any(item => item.Component is SceneSpawn)))
            throw new InvalidDataException("A room can have just one player start.");
        var names = _room.Entities.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Enumerable.Range(0, count).Select(step =>
        {
            var name = template.Name;
            for (var suffix = 2; !names.Add(name); suffix++)
                name = $"{template.Name} {suffix}";
            var offset = step * .045;
            return preset.Place(Guid.NewGuid(), template.Transform with
            {
                X = Math.Clamp(template.Transform.X + offset, .05, .95),
                Y = Math.Clamp(template.Transform.Y + offset, .05, .95)
            }, name, template.Component);
        }).ToArray();
    }

    private SceneEntity ReadVariant(ScenePreset preset)
    {
        if (!Number(PresetWidth.Text, out var width) || !Number(PresetHeight.Text, out var height))
            throw new InvalidDataException("Enter a width and height from 0.025 to 1 room units.");
        var component = preset.Template.Component switch
        {
            WallPanel => new WallPanel((PanelColor)PresetChoice.SelectedIndex),
            SceneDoor => new SceneDoor(PresetChoice.SelectedIndex == 1),
            SceneLamp => new SceneLamp(PresetChoice.SelectedIndex == 1),
            SceneSwitch => new SceneSwitch(null, (SwitchBehavior)PresetChoice.SelectedIndex),
            SceneGoal => new SceneGoal(int.TryParse(PresetKeys.Text, out var keys) ? keys : -1),
            _ => preset.Template.Component
        };
        var template = preset.Template with
        {
            Name = PresetName.Text.Trim(),
            Transform = preset.Template.Transform with { Width = width, Height = height },
            Component = component
        };
        SceneValidation.ValidateEntity(template);
        return template;

        static bool Number(string value, out double number) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private void PlaceClicked(object sender, RoutedEventArgs e) => Try(() =>
    {
        Result = ConfigurePlacement();
        DialogResult = true;
    });

    private void OpenPresetClicked(object sender, RoutedEventArgs e) => Try(() =>
    {
        var dialog = new OpenFileDialog { Title = "Open a reusable preset", Filter = "PLATZ preset (*.platzpreset)|*.platzpreset" };
        if (dialog.ShowDialog(this) != true)
            return;
        using var file = File.OpenRead(dialog.FileName);
        if (file.Length > 24 * 1024)
            throw new InvalidDataException("Choose a PLATZ preset up to 24 KiB.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        var preset = ScenePreset.Decode(bytes);
        var existing = _presets.FindIndex(item => item.Id == preset.Id);
        if (existing < 0)
            _presets.Add(preset);
        else if (_presets[existing] != preset)
            throw new InvalidDataException("An open preset already uses this identity with different contents.");
        PresetSearch.Clear();
        Filter(preset.Id);
    });

    private void SaveVariantClicked(object sender, RoutedEventArgs e) => Try(() =>
    {
        if (PresetList.SelectedItem is not PresetCard { Preset: { } parent })
            return;
        var template = ReadVariant(parent);
        var preset = ScenePreset.FromObject(template with { PresetId = parent.Id });
        if (SavePreset(this, preset))
        {
            _presets.Add(preset);
            PresetSearch.Clear();
            Filter(preset.Id);
            PresetStatus.Text = "Saved reusable variant.";
        }
    });

    internal static bool SavePreset(Window owner, ScenePreset preset)
    {
        var bytes = preset.Encode();
        var safeName = string.Concat(preset.Title.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var dialog = new SaveFileDialog { Title = "Save a reusable preset", Filter = "PLATZ preset (*.platzpreset)|*.platzpreset",
            AddExtension = true, DefaultExt = ScenePreset.Extension, FileName = safeName + ScenePreset.Extension };
        if (dialog.ShowDialog(owner) != true)
            return false;
        var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, dialog.FileName, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return true;
    }

    private void Try(Action work)
    {
        try { work(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            PresetStatus.Text = error.Message;
        }
    }

    internal static Geometry ComponentIcon(SceneComponent component) => Geometry.Parse(component switch
    {
        WallPanel => "M1,2 H27 V20 H1 Z M1,8 H27 M1,14 H27 M10,2 V8 M18,8 V14 M10,14 V20",
        SceneDoor => "M3,22 V1 H24 V22 M7,22 V4 H20 V22 M15,13 H17",
        SceneLamp => "M8,12 Q3,8 7,3 Q13,-2 19,3 Q23,8 18,12 L16,16 H10 Z M10,19 H16 M11,22 H15",
        SceneSwitch => "M1,16 H27 V22 H1 Z M8,16 L18,5 M16,3 Q19,0 22,3 Q24,7 20,9",
        SceneSpawn => "M13,1 V24 M1,12 H25 M7,7 H19 V18 H7 Z",
        SceneKey => "M10,3 A5,5 0 1 1 2,11 A5,5 0 0 1 10,3 M11,10 L23,22 M19,18 L22,15 M22,21 L25,18",
        SceneGoal => "M4,24 V2 M4,3 H24 L19,8 L24,13 H4 M1,24 H11",
        _ => "M1,3 H26 V22 H1 Z M2,19 L10,10 L16,17 L19,13 L25,20"
    });

    private sealed record PresetCard(ScenePreset Preset, Geometry Icon);
}
