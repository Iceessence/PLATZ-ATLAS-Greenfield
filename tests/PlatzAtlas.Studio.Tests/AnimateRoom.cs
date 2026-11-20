using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.RoomView;
using PlatzAtlas.Runtime;
using PlatzAtlas.Studio;

internal static partial class Program
{
    private static void AnimateRoom(string directory)
    {
        var source = Path.Combine(directory, "switchback-presets.platz");
        var path = Path.Combine(directory, "switchback-animation.platz");
        var package = Path.Combine(directory, "switchback-animation.platzroom");
        var window = new MainWindow(RendererPreference.CpuReference, false, false, source, path,
            new StudioDiagnostics(Path.Combine(directory, "animation-studio.ndjson")));
        window.Show();
        try
        {
            var editor = new PixelEditorWindow(null) { Owner = window };
            editor.Show();
            ProjectAsset asset;
            try
            {
                Idle(editor);
                Click(editor, "Pixels64");
                Find<TextBox>(editor, "PixelColor").Text = "264658FF";
                var tools = Find<UniformGrid>(editor, "ToolButtons");
                ((Button)tools.Children.OfType<Button>().Single(item => Equals(item.Tag, "Fill"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.HandleCanvasKey(Key.Space);
                Click(editor, "ToolPaint");
                Find<TextBox>(editor, "PixelColor").Text = "6DE2A7FF";
                Stroke(72, 195, 156, 195);
                Stroke(156, 195, 198, 105);
                Stroke(198, 105, 233, 267);
                Stroke(233, 267, 273, 195);
                Stroke(273, 195, 315, 195);
                var frameLength = Find<TextBox>(editor, "FrameDuration");
                frameLength.Text = "1800";
                Click(editor, "SetDuration");
                Click(editor, "DuplicateFrame");
                Find<TextBox>(editor, "PixelColor").Text = "FFCB78FF";
                Stroke(93, 95, 291, 95);
                Stroke(93, 285, 291, 285);
                frameLength.Text = "1800";
                Click(editor, "SetDuration");
                Capture(editor, Path.Combine(directory, "pixel-animation.png"));
                editor.WriteSheet(Path.Combine(directory, "pier-pulse-sheet.zip"));
                asset = editor.CreateAsset("Pier pulse");
                using var archive = ZipFile.OpenRead(Path.Combine(directory, "pier-pulse-sheet.zip"));
                Check(archive.GetEntry("sprites.png") is { Length: > 0 } && archive.GetEntry("animation.json") is { Length: > 0 },
                    "native timeline exported a PNG sheet and timing metadata");
                void Stroke(double x, double y, double endX, double endY)
                {
                    Check(editor.BeginPointerStroke(new Point(x, y)), "stroke begins on the animated frame");
                    editor.ContinuePointerStroke(new Point(endX, endY));
                    Find<Grid>(editor, "PixelSurface").ReleaseMouseCapture();
                }
            }
            finally { editor.Close(); }

            window.AddEditedAsset(asset);
            Click(window, "PlaceAsset");
            Find<TextBox>(window, "SceneX").Text = ".78";
            Find<TextBox>(window, "SceneY").Text = ".30";
            Click(window, "ApplyTransform");
            MainWindow.TogglePlayCommand.Execute(null, window);
            Click(window, "SceneUseSwitch");
            Check(Find<TextBlock>(window, "PlayResultText").Text.Contains("Closed → Open"), "existing game switch still works with animated art");
            Capture(window, Path.Combine(directory, "animation-play-first.png"));
            Wait(1900);
            Capture(window, Path.Combine(directory, "animation-play-second.png"));
            Check(!File.ReadAllBytes(Path.Combine(directory, "animation-play-first.png"))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "animation-play-second.png"))), "WPF playtest pixels change while the room is idle");
            MainWindow.TogglePlayCommand.Execute(null, window);
            ApplicationCommands.SaveAs.Execute(null, window);
            var reopened = ProjectSession.Open(path);
            var original = reopened.Current.Assets.Single(item => item.Id == asset.Id);
            var clip = PixelClip.Read(original);
            Check(clip.Width == 64 && clip.Frames.Count == 2 && clip.Frames[1].DurationMs == 1800, "timeline is editable after reopen");
            var room = RuntimeRoom.FromProject(reopened.ReadSavedSnapshot());
            RoomPackage.Save(package, room);
            var portable = RoomPackage.Open(package);
            var sprite = portable.Entities!.Single(item => item.Component is SceneSprite { AssetId: var id } && id == asset.Id);
            var frame1 = SceneArt.Create(portable.ProjectId, portable.SourceRevision, [sprite], null, assets: portable.RasterAssets);
            var frame2 = SceneArt.Create(portable.ProjectId, portable.SourceRevision, [sprite], null, assets: portable.RasterAssets, animationMs: 2000);
            Check(!frame1.Sprites.Single().Image.Rgba.Span.SequenceEqual(frame2.Sprites.Single().Image.Rgba.Span),
                "the exported renderer receives different frame pixels at the authored times");
            Console.WriteLine("PASS Windows Pixel Editor 64×64 frame editing → PNG sheet → Studio Play animation and switch → save/reopen and portable animation");
        }
        finally
        {
            if (!File.Exists(path))
            {
                if (Find<Button>(window, "PlayToggle").Content as string == "Stop")
                    MainWindow.TogglePlayCommand.Execute(null, window);
                ApplicationCommands.SaveAs.Execute(null, window);
            }
            window.Close();
        }
    }

    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
