using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class AppearanceChecks
{
    public static void Run(string directory)
    {
        var editor = ProjectSession.NewScene();
        var pid = editor.Current.ProjectId;
        var wall = new SceneEntity(Guid.NewGuid(), "Panel", new(.35, .40, .2, .15, 25), new WallPanel(PanelColor.Sand));
        var lamp = new SceneEntity(Guid.NewGuid(), "Lamp", new(.40, .40, .10, .10, 0), new SceneLamp(true));
        editor.Apply(new AddSceneEntity(pid, 1, wall));
        editor.Apply(new AddSceneEntity(pid, 2, lamp));
        var copyId = Guid.NewGuid();
        editor.Apply(new DuplicateSceneEntity(pid, 3, copyId, lamp.Id));
        var copy = editor.Current.Scene!.Entities.Single(e => e.Id == copyId);
        Require(copy.Component == lamp.Component && copy.Id != lamp.Id && copy.Name == "Lamp copy" &&
            copy.Transform.X > lamp.Transform.X, "duplication preserves properties while minting a new identity");
        editor.Apply(new ConfigureRoomAppearance(pid, 4, editor.Current.SceneId, "Tidal workshop", 0x542b75));
        editor.Apply(new ShiftSceneLayer(pid, 5, wall.Id, Forward: true));
        Require(SceneAppearance.Ordered(editor.Current.Scene!.Entities, editor.Current.Scene.Appearance?.PaintOrder)
            .ElementAt(1).Id == wall.Id, "one forward step swaps immediate paint neighbors");
        var path = Path.Combine(directory, "room-appearance.platz");
        editor.Save(path);
        var reopened = ProjectSession.Open(path);
        Require(reopened.Current.DisplayName == "Tidal workshop" && reopened.Current.SchemaVersion == 5 &&
            reopened.Current.Scene!.Appearance?.BackgroundRgb == 0x542b75, "styled room identity reopens");
        Require(reopened.Undo() && reopened.Current.Scene!.Appearance!.PaintOrder is null &&
            reopened.Redo() && reopened.Current.Scene!.Appearance!.PaintOrder![1] == wall.Id, "paint history survives reopen");
        reopened.Apply(new DeleteSceneEntity(pid, reopened.Current.Revision, lamp.Id));
        reopened.Save();
        var trimmed = RoomPackage.Decode(RoomPackage.Encode(RuntimeRoom.FromProject(reopened.ReadSavedSnapshot())));
        Require(trimmed.Version == 3 && trimmed.Name == "Tidal workshop" && trimmed.BackgroundRgb == 0x542b75 &&
            trimmed.PaintOrder!.Count == 2 && !trimmed.PaintOrder.Contains(lamp.Id), "export filters deleted IDs from style history");
        var restored = ProjectSession.Open(path);
        Require(restored.Undo() && restored.Current.Scene!.Entities.Contains(lamp) &&
            restored.Current.Scene.Appearance!.PaintOrder!.Contains(lamp.Id), "deleted object retains its order after recovery");
        Require(ProjectSession.OpenPrevious(path).Current.DisplayName == "Tidal workshop", "recover prior styled room");
        Console.WriteLine("PASS duplication with stable source, room name, floor color, paint order, durable undo/redo and package readback");
    }

    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
