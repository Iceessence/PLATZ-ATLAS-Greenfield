using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.Runtime;
using PlatzAtlas.Studio;

internal static partial class Program
{
    private static void PlanInStudio(string directory)
    {
        var path = Path.Combine(directory, "birch-gate.platz");
        var package = Path.Combine(directory, "birch-gate.platzroom");
        var window = new MainWindow(RendererPreference.CpuReference, false, false, null, path,
            new StudioDiagnostics(Path.Combine(directory, "plan-studio.ndjson")));
        window.Show();
        try
        {
            Idle(window);
            Check(Find<ListBox>(window, "Objects").Items.Count == 0, "new unsaved project");
            Click(window, "SceneAtlasAccess");
            Find<TextBox>(window, "SceneAtlasQuestion").Text = "Build a keyed gate named Birch on the left";
            Click(window, "SceneAtlasAsk");
            Click(window, "SceneAtlasPreview");
            Check(Find<Border>(window, "SceneAtlasPreviewBanner").IsVisible && Find<ListBox>(window, "Objects").Items.Count == 0 &&
                !File.Exists(path), "Entire plan preview is detached from the outliner and disk");
            Capture(window, Path.Combine(directory, "atlas-gate-preview.png"));
            Click(window, "SceneAtlasCancel");
            Check(Find<ListBox>(window, "Objects").Items.Count == 0, "Cancel preserves the original room");
            Click(window, "SceneAtlasPreview");
            Click(window, "SceneAtlasConfirm");
            Check(Find<ListBox>(window, "Objects").Items.Count == 7 &&
                Find<TextBlock>(window, "SceneAtlasReceiptText").Text.Contains("8 grouped edits"), "Approval creates seven linked objects in one group");
            MainWindow.TogglePlayCommand.Execute(null, window);
            for (var count = 0; count < 6; count++) Press(Key.Up);
            Check(Find<TextBlock>(window, "PlayBannerText").Text.Contains("Keys 1/1"), "Generated key collected in Studio");
            Press(Key.E);
            Check(Find<TextBlock>(window, "PlayResultText").Text.Contains("Birch door · Closed → Open"), "Generated switch opens its named door");
            for (var count = 0; count < 12; count++) Press(Key.Up);
            Check(Find<TextBlock>(window, "PlayBannerText").Text.Contains("Goal complete · Birch exit"), "Generated exit is reachable");
            Capture(window, Path.Combine(directory, "atlas-gate-won.png"));
            MainWindow.TogglePlayCommand.Execute(null, window);
            ApplicationCommands.Undo.Execute(null, window);
            Check(Find<ListBox>(window, "Objects").Items.Count == 0 && !File.Exists(path), "One Undo returns to a blank unsaved room");
            ApplicationCommands.Redo.Execute(null, window);
            Check(Find<ListBox>(window, "Objects").Items.Count == 7, "One Redo restores the linked room");
            ApplicationCommands.Save.Execute(null, window);
            var saved = ProjectSession.Open(path);
            RoomPackage.Save(package, RuntimeRoom.FromProject(saved.ReadSavedSnapshot()));
            Check(saved.LatestScenePlan is { Changes: 8 } && saved.Undo() && saved.Current.Scene!.Entities.Count == 0,
                "Receipt and grouped Undo survive reopen and explicit export");
            Console.WriteLine("PASS unsaved ATLAS natural-language gate → whole ghost → cancel/approve → win → grouped undo/redo → saved plan receipt");
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

        void Press(Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
}
