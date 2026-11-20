using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.Studio;

internal static partial class Program
{
    private static void PresetsInStudio(string directory)
    {
        var source = Path.Combine(directory, "switchback.platz");
        var copy = Path.Combine(directory, "switchback-presets.platz");
        var room = ProjectSession.Open(source);
        var window = new MainWindow(RendererPreference.CpuReference, false, false, source, copy,
            new StudioDiagnostics(Path.Combine(directory, "presets-studio.ndjson")));
        window.Show();
        try
        {
            var browser = new PresetBrowserWindow(StarterPresets.Items.ToList(), room.Current.Scene!) { Owner = window };
            browser.Show();
            try
            {
                Find<TextBox>(browser, "PresetSearch").Text = "signal lamp";
                Check(Find<ListBox>(browser, "PresetList").Items.Count == 1, "search finds the matching lamp preset");
                Find<TextBox>(browser, "PresetName").Text = "Pier lamp";
                Find<TextBox>(browser, "PresetWidth").Text = ".12";
                Find<TextBox>(browser, "PresetHeight").Text = ".18";
                Find<TextBox>(browser, "PresetCount").Text = "2";
                Find<ComboBox>(browser, "PresetChoice").SelectedIndex = 0;
                Capture(browser, Path.Combine(directory, "preset-browser.png"));
                window.PlacePresets(browser.ConfigurePlacement());
            }
            finally { browser.Close(); }
            var id = Find<TextBlock>(window, "ObjectId").Text;
            Check(id.StartsWith('#') && id.Length == 7, "objects have visible numeric IDs");
            Check(!Find<Expander>(window, "RoomAppearanceEditor").IsVisible, "room appearance is separate from the object Inspector");
            Click(window, "FrameSelection");
            Check(!Find<TextBlock>(window, "ViewportCoordinates").Text.StartsWith("100", StringComparison.Ordinal),
                "frame selected zooms the viewport");
            Click(window, "ChangeLight");
            Click(window, "SceneAtlasAccess");
            Find<TextBox>(window, "SceneAtlasQuestion").Text = $"Inspect {id}";
            Click(window, "SceneAtlasAsk");
            Check(Find<TextBlock>(window, "SceneAtlasResult").Text.Contains("Pier lamp 2") &&
                Find<TextBlock>(window, "SceneAtlasResult").Text.Contains("State is on"), "ATLAS resolves the ID and current lamp state");
            Capture(window, Path.Combine(directory, "studio-workspace.png"));
            ApplicationCommands.SaveAs.Execute(null, window);
            var saved = ProjectSession.Open(copy);
            var entities = saved.Current.Scene!.Entities;
            Check(entities.Count == 5 && entities.Single(item => item.Name == "Pier lamp 2") is
                { PresetId: not null, Transform: { Width: .12, Height: .18 }, Component: SceneLamp { IsOn: true } },
                "edited variant persisted alongside the pre-existing room");
            Console.WriteLine("PASS Studio existing room → search preset → configure two instances → frame selection → edit and inspect by number → Save As and reopen");
        }
        finally
        {
            if (!File.Exists(copy))
                ApplicationCommands.SaveAs.Execute(null, window);
            window.Close();
        }
    }
}
