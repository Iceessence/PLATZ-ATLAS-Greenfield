using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.Studio;

internal static partial class Program
{
    private static void WorkingRoom(string directory)
    {
        var path = Path.Combine(directory, "switchback.platz");
        var window = new MainWindow(RendererPreference.CpuReference, false, false, null, path,
            new StudioDiagnostics(Path.Combine(directory, "studio.ndjson")));
        window.Show();
        try
        {
            Idle(window);
            Check(Label("RevisionLabel").Contains("Unsaved"), "Studio starts with a blank unsaved room");
            Click(window, "AddWall");
            Place(".12", ".4");
            Click(window, "AddDoor");
            Place(".5", ".6");
            Click(window, "AddSwitch");
            Place(".5", ".85");
            Find<ComboBox>(window, "SwitchEntityTarget").SelectedIndex = 1;
            Click(window, "ApplySwitchLink");
            var tabs = Find<TabControl>(window, "SideTabs");
            tabs.SelectedItem = Find<TabItem>(window, "SceneAtlasTab");
            Click(window, "SceneAtlasAccess");
            Ask("What does Switch control?");
            Check(Label("SceneAtlasResult").Contains("Controls Door"), "ATLAS reads the unsaved switch link");
            Ask("Rename Door to Old exit");
            Click(window, "SceneAtlasPreview");
            Check(Find<Border>(window, "SceneAtlasPreviewBanner").IsVisible, "Detached preview opens before a save");
            Capture(window, Path.Combine(directory, "live-atlas-preview.png"));
            tabs.SelectedItem = Find<TabItem>(window, "InspectorTab");
            Field("SceneName", "Dock console");
            Click(window, "RenameObject");
            Check(!Find<Border>(window, "SceneAtlasPreviewBanner").IsVisible &&
                Label("SceneAtlasResult").Contains("working room changed"), "A manual edit invalidates the old preview");
            Click(window, "SceneAtlasConfirm");
            Check(!File.Exists(path), "An invalidated confirmation does not write a file");

            tabs.SelectedItem = Find<TabItem>(window, "SceneAtlasTab");
            Click(window, "SceneAtlasAccess");
            Ask("Rename Door to Dock shutter");
            Click(window, "SceneAtlasPreview");
            Click(window, "SceneAtlasConfirm");
            Check(Find<Expander>(window, "SceneAtlasReceipt").Visibility == Visibility.Visible &&
                Label("SceneAtlasReceiptText").Contains("Applied to working room") && !File.Exists(path),
                "ATLAS applied to the unsaved room with a receipt");
            Check(!MainWindow.ExportRoomCommand.CanExecute(null, window), "Export stays pinned to an explicit saved head");

            MainWindow.TogglePlayCommand.Execute(null, window);
            Check(Label("PlayBannerText").StartsWith("Playtest · Working", StringComparison.Ordinal), "Play starts from the unsaved revision");
            Click(window, "SceneUseSwitch");
            Check(Label("PlayResultText").Contains("Dock shutter · Closed → Open"), "Switch operates on the unsaved dock shutter in Play");
            Capture(window, Path.Combine(directory, "live-play.png"));
            Click(window, "RestartPlay");
            Check(Label("PlayResultText") == "", "Restart resets the working playtest");
            Click(window, "SceneUseSwitch");
            Check(Label("PlayResultText").Contains("Closed → Open"), "Frozen restart restores the initial door state");
            MainWindow.TogglePlayCommand.Execute(null, window);
            Check(Label("RevisionLabel").Contains("Unsaved") && !File.Exists(path), "Stop returns to unsaved editing without publishing");
            ApplicationCommands.Save.Execute(null, window);
            var stored = ProjectSession.Open(path);
            Check(stored.Current.Scene!.Entities.Any(item => item is { Name: "Dock shutter", Component: SceneDoor { IsOpen: false } }) &&
                stored.Current.Scene.Entities.Any(item => item.Name == "Dock console") &&
                stored.LatestSceneCommit is not null, "Only Save stores the manual edit, receipt and isolated door state");
            Check(MainWindow.ExportRoomCommand.CanExecute(null, window), "The exact saved revision enables export");
            Console.WriteLine("PASS Windows Studio blank → unsaved ATLAS → stale preview → approved edit → Play and Restart → explicit Save and reopen");
        }
        finally
        {
            if (!File.Exists(path))
            {
                if (Find<Button>(window, "PlayToggle").Content as string == "Stop")
                    MainWindow.TogglePlayCommand.Execute(null, window);
                ApplicationCommands.Save.Execute(null, window);
            }
            window.Close();
        }

        string Label(string name) => Find<TextBlock>(window, name).Text;
        void Field(string name, string text) => Find<TextBox>(window, name).Text = text;
        void Ask(string text)
        {
            Field("SceneAtlasQuestion", text);
            Click(window, "SceneAtlasAsk");
        }
        void Place(string x, string y)
        {
            Field("SceneX", x);
            Field("SceneY", y);
            Click(window, "ApplyTransform");
        }
    }

    private static void Idle(Window window) => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Capture(Window window, string path)
    {
        Idle(window);
        var source = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        source.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
